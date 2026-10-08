/*
===============================================================================
QTO.cs
Civil 3D 2026
QTO - v0.16 | SAÍDA POR DWG/CORREDOR + MEMÓRIA VINTAGE + CONSOLIDAÇÃO
===============================================================================

OBJETIVO DESTA VERSÃO
---------------------
Consolidar a memória de cálculo do QTO por intervalos de estaca, mantendo o
motor nativo do Civil 3D já validado e fechando a apresentação do XLSX em um
padrão vintage técnico, claro e adequado para auditoria, filtros, PROCX e uso
por orçamento/engenharia.

A v0.16 mantém o motor nativo validado nas versões anteriores, mas acrescenta
uma camada crítica de isolamento por Corridor no XML Detailed Report. O layout
vintage aprovado permanece preservado: fonte Consolas, marrom #6B5B4D, bege
#D8CBB8, creme #F4F0E6/#F1EBDD e linhas #A99A88.

NOVIDADES DA v0.16
------------------
- suporta testar o QTO em DWG contendo VÁRIOS Corridors sem misturar os quantitativos;
- o usuário continua selecionando um Corridor e um Alignment de referência no início;
- após o GenerateXMLReport, cada item detalhado do XML é filtrado pelo
  AutoCADObjectHandle do Corridor selecionado antes de qualquer soma;
- quando um item detalhado não traz Handle, a v0.14 aceita um fallback seguro
  pelo baselineName, somente se ele pertencer às Baselines/Alignments do Corridor;
- se o Handle do Corridor não aparecer no TOTAL_ALIGNMENT, a rotina ABORTA a
  exportação em vez de gerar uma memória potencialmente misturada;
- TOTAL_ALIGNMENT, DrawingExtent e todos os intervalos usam o MESMO filtro;
- o Excel passa a deixar explícitos como fórmulas: Soma por Estacas, diferenças,
  percentuais, status, Área/Volume/Comprimento e a Chave para PROCX;
- continua gerando uma pasta estável QTO_<DWG>__<CORRIDOR> e o
  RESUMO_ORCAMENTO.csv limpo para consolidação externa;
- QTOESTUDO continua gerando SOMENTE a memória do Corridor selecionado;
- para itens lineares oriundos de Corridor Feature Line, a v0.16 abandona a
  correção geométrica ampla da v0.15 (que podia duplicar Baselines sobrepostas)
  e usa um reparo conservador por continuidade LT/RT. Só corrige um intervalo
  isolado quando os vizinhos têm os dois lados e o déficit fecha com o total
  nativo do Alignment;
- se o XLSX de memória estiver aberto no Excel, a rotina não aborta no fim:
  salva a nova memória com sufixo __ATUALIZADA_<data_hora>.xlsx e avisa na linha
  de comando.

MOTOR PRINCIPAL DO QTOESTUDO
----------------------------
- usa QTOUtility.GenerateXMLReport;
- gera um Detailed Report nativo para o alinhamento inteiro;
- gera um Detailed Report nativo para cada intervalo de estacas;
- soma os intervalos por Pay Item;
- compara a soma contra o Takeoff nativo do alinhamento inteiro;
- tenta também gerar o Takeoff global do desenho (DrawingExtent);
- usa tolerância de 5% para a auditoria;
- preserva todos os XMLs gerados e cria CSVs de intervalos e auditoria;
- gera XLSX válido e formatado no padrão vintage da memória de cálculo de referência (marrom, bege e creme);
- a primeira aba é uma base tabular plana, com cabeçalho na linha 1 e coluna CHAVE,
  pensada para PROCX/filtros/tabelas dinâmicas;
- inclui resumo/auditoria vintage com Corredor + Alignment no cabeçalho técnico;
- mantém os elementos geométricos do Alignment na própria aba Resumo QTO;
- mantém aba separada de Metadados.

DIMENSÃO
--------
A v0.16 NÃO força 2D ou 3D. O relatório total e todos os relatórios fracionados
são gerados pelo MESMO QTOUtility, na mesma execução e sob as mesmas
configurações. Portanto, a comparação é feita na mesma dimensão retornada pelo
motor nativo do Civil 3D.

ABRANGÊNCIA LATERAL
-------------------
A opção Automática/Offset permanece REMOVIDA na v0.16. Na API QTO 2026,
AlignmentOffsetId não fornece um filtro lateral funcional para este fluxo, então
manter a pergunta no comando poderia induzir a pensar que ela alterava o cálculo.

O fracionamento continua usando o motor nativo por:
    AlignmentId + StartStation + EndStation

Além disso, na v0.16, o XML detalhado é filtrado prioritariamente pelo
AutoCADObjectHandle do Corridor selecionado. Itens sem Handle podem entrar pelo
baselineName apenas quando esse nome pertence ao próprio Corridor. Assim, a
abrangência longitudinal vem do Alignment e a separação entre Corridors usa
Handle + fallback controlado por Baseline.
A soma dos intervalos filtrados é comparada com o Takeoff filtrado do Alignment,
com tolerância de 5%. O clipping geométrico próprio permanece como plano B.

COMANDOS
--------
QTOESTUDO
    Executa o fracionamento nativo por estacas e a auditoria de 5%.

QTOGEOTESTE
    Mantém o teste geométrico 2D validado da v0.6 (diagnóstico 0+0.00 -> 1+0.00).

TESTEQTO
    Confirma carregamento da classe.

QTOHELP
    Exibe ajuda e critérios desta versão.

SEGURANÇA
---------
- não altera Corridor, Alignment, assemblies, links, shapes ou Pay Items;
- apenas lê o desenho e gera arquivos de relatório;
- cada XML é copiado imediatamente para uma pasta exclusiva do estudo para
  evitar sobrescrita dos arquivos temporários do QTOUtility.

===============================================================================
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcRuntime = Autodesk.AutoCAD.Runtime;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;


namespace Civil3D2026Plugin.Modules.QTO
{
    public class QtoCommands
    {
        // =====================================================================
        // VERSÃO
        // =====================================================================

        public const string Versao = C3DVersions.Qto;


        // =====================================================================
        // TOLERÂNCIAS
        // =====================================================================

        private const double ToleranciaEstacao = 0.000001;

        private const double ToleranciaOffset = 0.000001;

        private const double ToleranciaAuditoriaPercentual = 5.0;

        // =====================================================================
        // TESTE DE CARREGAMENTO
        // =====================================================================

        [AcRuntime.CommandMethod(C3DCommands.Qto.Teste)]
        public void TesteQto()
        {
            Document? doc =
                AcApp.DocumentManager.MdiActiveDocument;

            if (doc == null)
                return;


            doc.Editor.WriteMessage(
                $"\n{C3DCommands.Qto.Teste}: QTO v{Versao} carregado e disponível.");
        }


        // =====================================================================
        // COMANDO PRINCIPAL DE TESTE
        // =====================================================================

        [AcRuntime.CommandMethod(C3DCommands.Qto.GeoTeste)]
        public void QtoGeoTeste()
        {
            Document? doc =
                AcApp.DocumentManager.MdiActiveDocument;

            if (doc == null)
                return;


            Database db =
                doc.Database;


            Editor ed =
                doc.Editor;


            try
            {
                ed.WriteMessage(
                    "\n\n====================================================" +
                    $"\n QTOGEOTESTE v{Versao}" +
                    "\n GEOMETRIA 2D DIRETA DO CORRIDOR" +
                    "\n====================================================");


                CivilDocument civilDoc =
                    CivilDocument.GetCivilDocument(
                        db);


                // =============================================================
                // 1. LOCALIZAR CORRIDOR
                // =============================================================

                if (civilDoc.CorridorCollection.Count == 0)
                {
                    ed.WriteMessage(
                        "\n[ERRO] Nenhum Corridor encontrado no desenho.\n");

                    return;
                }


                if (civilDoc.CorridorCollection.Count > 1)
                {
                    ed.WriteMessage(
                        "\n[ERRO] Existem " +
                        civilDoc.CorridorCollection.Count +
                        " corredores no desenho." +
                        "\n" +
                        "\nPara este teste deixe somente um corredor.\n");

                    return;
                }


                ObjectId corridorId =
                    civilDoc.CorridorCollection[0];


                using Transaction tr =
                    db.TransactionManager.StartTransaction();


                CivilCorridor corridor =
                    (CivilCorridor)tr.GetObject(
                        corridorId,
                        OpenMode.ForRead);


                // =============================================================
                // 2. LOCALIZAR BASELINE / ALIGNMENT PRINCIPAL
                //
                // Por enquanto:
                // baseline baseado em Alignment de MAIOR extensão.
                // =============================================================

                Baseline? baselinePrincipal =
                    null;


                Alignment? alignmentPrincipal =
                    null;


                double maiorExtensao =
                    double.MinValue;


                foreach (
                    Baseline baseline
                    in corridor.Baselines)
                {
                    ObjectId alignmentId;


                    try
                    {
                        alignmentId =
                            baseline.AlignmentId;
                    }
                    catch
                    {
                        continue;
                    }


                    if (alignmentId.IsNull)
                        continue;


                    Alignment alignment =
                        (Alignment)tr.GetObject(
                            alignmentId,
                            OpenMode.ForRead);


                    double extensao =
                        alignment.EndingStation -
                        alignment.StartingStation;


                    if (extensao > maiorExtensao)
                    {
                        maiorExtensao =
                            extensao;


                        baselinePrincipal =
                            baseline;


                        alignmentPrincipal =
                            alignment;
                    }
                }


                if (
                    baselinePrincipal == null ||
                    alignmentPrincipal == null)
                {
                    ed.WriteMessage(
                        "\n[ERRO] Não foi possível localizar o baseline principal.\n");

                    return;
                }


                Baseline baselineAlvo =
                    baselinePrincipal;


                Alignment alignmentAlvo =
                    alignmentPrincipal;


                ed.WriteMessage(
                    "\n\nCorredor:" +
                    "\n  " +
                    corridor.Name +
                    "\n" +
                    "\nAlinhamento:" +
                    "\n  " +
                    alignmentAlvo.Name);


                // =============================================================
                // 3. INTERVALO DESTE TESTE
                // =============================================================

                double inicio =
                    0.0;


                double fim =
                    20.0;


                // Caso o alinhamento não contenha 0-20,
                // usa seus primeiros 20 metros.

                if (
                    alignmentAlvo.StartingStation > inicio ||
                    alignmentAlvo.EndingStation < fim)
                {
                    inicio =
                        alignmentAlvo.StartingStation;


                    fim =
                        Math.Min(
                            inicio + 20.0,
                            alignmentAlvo.EndingStation);
                }


                ed.WriteMessage(
                    "\n\nIntervalo:" +
                    "\n  " +
                    FormatarEstaca(inicio) +
                    " -> " +
                    FormatarEstaca(fim));


                // =============================================================
                // 4. REGULARIZAÇÃO
                //
                // LINK CODE -> ÁREA 2D
                // =============================================================

                SortedDictionary<double, List<Link2D>> regularizacao =
                    ColetarLinksPorEstacao(
                        baselineAlvo,
                        "Regularizaçao",
                        inicio,
                        fim,
                        ed);


                double areaRegularizacao =
                    CalcularAreaLinks(
                        regularizacao,
                        alignmentAlvo,
                        ed);


                // =============================================================
                // 5. NEW JERSEY
                //
                // POINT CODE -> CORRIDOR FEATURE LINE -> COMPRIMENTO 2D
                // =============================================================

                double comprimentoNj =
                    CalcularComprimentoFeatureLine2D(
                        baselineAlvo,
                        "NJ_QTO",
                        inicio,
                        fim,
                        ed);


                // =============================================================
                // 6. RESULTADO
                // =============================================================

                ed.WriteMessage(
                    "\n\n====================================================" +
                    "\n RESULTADO GEOMÉTRICO 2D" +
                    "\n====================================================");


                ed.WriteMessage(
                    "\nRegularizaçao:" +
                    "\n  Área calculada = " +
                    areaRegularizacao.ToString(
                        "0.000000",
                        CultureInfo.InvariantCulture) +
                    " m²" +
                    "\n  Esperado       ≈ 290.500000 m²");


                ed.WriteMessage(
                    "\n\nNJ_QTO:" +
                    "\n  Comprimento calculado = " +
                    comprimentoNj.ToString(
                        "0.000000",
                        CultureInfo.InvariantCulture) +
                    " m" +
                    "\n  Esperado              ≈ 38.000000 m");


                ed.WriteMessage(
                    "\n\nDiferenças:" +
                    "\n  Regularização = " +
                    (areaRegularizacao - 290.50)
                        .ToString(
                            "+0.000000;-0.000000;0.000000",
                            CultureInfo.InvariantCulture) +
                    " m²" +
                    "\n  NJ_QTO        = " +
                    (comprimentoNj - 38.0)
                        .ToString(
                            "+0.000000;-0.000000;0.000000",
                            CultureInfo.InvariantCulture) +
                    " m");


                ed.WriteMessage(
                    "\n====================================================\n");


                tr.Commit();
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage(
                    "\n\n====================================================" +
                    "\n QTOGEOTESTE - ERRO" +
                    "\n====================================================" +
                    "\n" +
                    ex.Message +
                    "\n" +
                    "\nTipo:" +
                    "\n" +
                    ex.GetType().FullName +
                    "\n====================================================\n");
            }
        }



        // =====================================================================
        // QTOESTUDO v0.14
        //
        // OBJETIVO:
        // testar o comportamento do próprio Takeoff do Civil 3D quando
        // fracionado por estação e comparar a soma com o total nativo.
        // =====================================================================

        [AcRuntime.CommandMethod(C3DCommands.Qto.Estudo)]
        public void QtoEstudo()
        {
            Document? doc =
                AcApp.DocumentManager.MdiActiveDocument;

            if (doc == null)
                return;

            Database db = doc.Database;
            Editor ed = doc.Editor;

            try
            {
                CivilDocument civilDoc =
                    CivilDocument.GetCivilDocument(db);

                ed.WriteMessage(
                    "\n\n====================================================" +
                    $"\n QTOESTUDO v{Versao}" +
                    "\n QTO POR ESTACA + FILTRO DE CORRIDOR + EXCEL" +
                    "\n====================================================" +
                    "\nCorridors encontrados no DWG: " + civilDoc.CorridorCollection.Count +
                    "\nEsta execução processará SOMENTE o Corridor selecionado.");

                // -------------------------------------------------------------
                // 1. Selecionar Corridor.
                // -------------------------------------------------------------

                PromptEntityOptions peCorridor =
                    new PromptEntityOptions(
                        "\nSelecione o Corridor a estudar: ");

                peCorridor.SetRejectMessage(
                    "\nSelecione um Corridor do Civil 3D.");

                peCorridor.AddAllowedClass(
                    typeof(CivilCorridor),
                    true);

                PromptEntityResult prCorridor =
                    ed.GetEntity(peCorridor);

                if (prCorridor.Status != PromptStatus.OK)
                    return;

                // -------------------------------------------------------------
                // 2. Selecionar Alignment de referência.
                //    Ele precisa pertencer a uma baseline do Corridor.
                // -------------------------------------------------------------

                PromptEntityOptions peAlignment =
                    new PromptEntityOptions(
                        "\nSelecione o Alignment de referência das estacas: ");

                peAlignment.SetRejectMessage(
                    "\nSelecione um Alignment do Civil 3D.");

                peAlignment.AddAllowedClass(
                    typeof(Alignment),
                    true);

                PromptEntityResult prAlignment =
                    ed.GetEntity(peAlignment);

                if (prAlignment.Status != PromptStatus.OK)
                    return;

                using Transaction tr =
                    db.TransactionManager.StartTransaction();

                CivilCorridor corridor =
                    (CivilCorridor)tr.GetObject(
                        prCorridor.ObjectId,
                        OpenMode.ForRead);

                string corridorHandle =
                    NormalizarHandle(
                        corridor.Handle.ToString());

                Alignment alignment =
                    (Alignment)tr.GetObject(
                        prAlignment.ObjectId,
                        OpenMode.ForRead);

                bool alignmentPertenceAoCorridor =
                    false;

                int totalBaselines = 0;
                List<string> baselinesInfo =
                    new List<string>();

                HashSet<string> nomesBaselinesCorridor =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);

                foreach (Baseline baseline in corridor.Baselines)
                {
                    totalBaselines++;

                    if (!string.IsNullOrWhiteSpace(baseline.Name))
                    {
                        nomesBaselinesCorridor.Add(
                            baseline.Name.Trim());
                    }

                    try
                    {
                        ObjectId id = baseline.AlignmentId;

                        if (!id.IsNull)
                        {
                            Alignment? al =
                                tr.GetObject(
                                    id,
                                    OpenMode.ForRead) as Alignment;

                            if (al != null)
                            {
                                baselinesInfo.Add(
                                    $"{baseline.Name} -> {al.Name}");

                                if (!string.IsNullOrWhiteSpace(al.Name))
                                {
                                    nomesBaselinesCorridor.Add(
                                        al.Name.Trim());
                                }
                            }

                            if (id == prAlignment.ObjectId)
                            {
                                alignmentPertenceAoCorridor =
                                    true;
                            }
                        }
                        else
                        {
                            baselinesInfo.Add(
                                $"{baseline.Name} -> (sem AlignmentId)");
                        }
                    }
                    catch
                    {
                        baselinesInfo.Add(
                            $"{baseline.Name} -> (baseline por Feature Line/offset ou não consultável)");
                    }
                }

                if (!alignmentPertenceAoCorridor)
                {
                    ed.WriteMessage(
                        "\n[ERRO] O Alignment selecionado não é uma baseline " +
                        "do Corridor selecionado.\n");

                    return;
                }

                if (!string.IsNullOrWhiteSpace(alignment.Name))
                {
                    nomesBaselinesCorridor.Add(
                        alignment.Name.Trim());
                }

                // -------------------------------------------------------------
                // 3. Intervalo padrão = 20 m.
                // -------------------------------------------------------------

                PromptDoubleOptions pdoIntervalo =
                    new PromptDoubleOptions(
                        "\nIntervalo entre estacas <20.00>: ");

                pdoIntervalo.AllowNegative = false;
                pdoIntervalo.AllowZero = false;
                pdoIntervalo.DefaultValue = 20.0;
                pdoIntervalo.UseDefaultValue = true;

                PromptDoubleResult pdrIntervalo =
                    ed.GetDouble(pdoIntervalo);

                if (pdrIntervalo.Status != PromptStatus.OK)
                    return;

                double passo =
                    pdrIntervalo.Value;

                // -------------------------------------------------------------
                // 4. Intervalo efetivo do Alignment.
                //
                // A opção de abrangência lateral permanece removida na v0.14.
                // AlignmentOffsetId não oferece filtro lateral funcional na
                // API QTO 2026; manter uma opção que não alterava o cálculo
                // seria enganoso. O motor permanece o QTO nativo por estação.
                // -------------------------------------------------------------

                double inicio =
                    alignment.StartingStation;

                double fim =
                    alignment.EndingStation;

                if (fim <= inicio + ToleranciaEstacao)
                {
                    ed.WriteMessage(
                        "\n[ERRO] O Alignment não possui intervalo de estação válido.\n");

                    return;
                }

                List<QtoAlignmentElementInfo> elementosAlinhamento =
                    ObterElementosAlinhamento(
                        alignment);

                double extensaoGeometrica =
                    alignment.Length;

                if (extensaoGeometrica <= ToleranciaEstacao)
                {
                    extensaoGeometrica =
                        Math.Abs(fim - inicio);
                }

                string alignmentTipo =
                    ObterStringPropriedade(
                        alignment,
                        "AlignmentType");

                string alignmentDescricao =
                    ObterStringPropriedade(
                        alignment,
                        "Description");

                int numeroIntervalos =
                    (int)Math.Ceiling(
                        (fim - inicio) /
                        passo);

                // -------------------------------------------------------------
                // 5. Pasta do estudo.
                // -------------------------------------------------------------

                string pastaBase;

                if (!string.IsNullOrWhiteSpace(db.Filename))
                {
                    pastaBase =
                        Path.GetDirectoryName(db.Filename) ??
                        Path.GetTempPath();
                }
                else
                {
                    pastaBase =
                        Path.GetTempPath();
                }

                string nomeDwg =
                    string.IsNullOrWhiteSpace(db.Filename)
                        ? "DESENHO_SEM_NOME"
                        : Path.GetFileNameWithoutExtension(db.Filename);

                string identificadorSaida =
                    LimparNomeArquivo(nomeDwg) +
                    "__" +
                    LimparNomeArquivo(corridor.Name);

                string pastaSaida =
                    Path.Combine(
                        pastaBase,
                        "QTO_" + identificadorSaida);

                Directory.CreateDirectory(pastaSaida);

                string pastaXml =
                    Path.Combine(
                        pastaSaida,
                        "XML");

                // A pasta do corredor é estável. Para evitar que uma nova
                // execução mantenha XMLs de intervalos de uma execução anterior,
                // a subpasta XML é recriada integralmente.
                if (Directory.Exists(pastaXml))
                {
                    Directory.Delete(
                        pastaXml,
                        true);
                }

                Directory.CreateDirectory(pastaXml);

                ed.WriteMessage(
                    "\n\nCorredor: " + corridor.Name +
                    "\nBaselines no Corridor: " + totalBaselines +
                    "\nAlignment de referência: " + alignment.Name +
                    "\nTipo do Alignment: " + (string.IsNullOrWhiteSpace(alignmentTipo) ? "não informado" : alignmentTipo) +
                    "\nExtensão geométrica: " + extensaoGeometrica.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR")) + " m" +
                    "\nElementos geométricos: " + elementosAlinhamento.Count +
                    "\nEstações: " + FormatarEstaca(inicio) +
                    " -> " + FormatarEstaca(fim) +
                    "\nPasso: " + passo.ToString("0.###", CultureInfo.InvariantCulture) + " m" +
                    "\nIntervalos previstos: " + numeroIntervalos +
                    "\nTolerância de auditoria: " +
                    ToleranciaAuditoriaPercentual.ToString("0.##", CultureInfo.InvariantCulture) + "%");

                ed.WriteMessage(
                    "\n\n[MOTOR] QTO nativo por AlignmentId + StartStation + EndStation." +
                    " A opção de largura lateral foi removida porque não alterava" +
                    " efetivamente o GenerateXMLReport na API QTO 2026.");

                ed.WriteMessage(
                    "\n[DIMENSÃO] Total e intervalos usam o mesmo QTOUtility " +
                    "sem forçar 2D/3D; portanto a auditoria compara a mesma " +
                    "dimensão nativa em toda a execução.\n");

                // -------------------------------------------------------------
                // 6. Total nativo do Alignment inteiro.
                // -------------------------------------------------------------

                ed.WriteMessage(
                    "\nGerando Takeoff nativo do Alignment inteiro...");

                string xmlTotalAlignment =
                    Path.Combine(
                        pastaXml,
                        "TOTAL_ALIGNMENT.xml");

                QtoReportData totalAlignment =
                    GerarRelatorioNativoPorEstacao(
                        prAlignment.ObjectId,
                        inicio,
                        fim,
                        xmlTotalAlignment,
                        corridorHandle,
                        nomesBaselinesCorridor);

                if (totalAlignment.ItensCorrespondentes <= 0)
                {
                    string handles =
                        totalAlignment.HandlesEncontrados.Count == 0
                            ? "(nenhum)"
                            : string.Join(", ", totalAlignment.HandlesEncontrados);

                    throw new InvalidOperationException(
                        "O XML nativo do QTO não contém itens que possam ser associados ao Corridor selecionado " +
                        "nem pelo Handle " + corridorHandle +
                        " nem pelos nomes das Baselines/Alignments do próprio Corridor. Handles encontrados: " +
                        handles +
                        ". O filtro por Corridor não pôde ser validado e a memória NÃO foi gerada.");
                }

                ed.WriteMessage(
                    "\n[FILTRO CORRIDOR] Handle: " + corridorHandle +
                    " | itens do relatório: " + totalAlignment.ItensDetalhados +
                    " | itens do Corridor: " + totalAlignment.ItensCorrespondentes +
                    " | fallback baseline sem Handle: " + totalAlignment.ItensIncluidosPorBaselineSemHandle +
                    " | sem Handle descartados: " + totalAlignment.ItensSemHandleDescartados +
                    " | descartados total: " + totalAlignment.ItensDescartados +
                    " | handles encontrados: " +
                    string.Join(", ", totalAlignment.HandlesEncontrados));

                // -------------------------------------------------------------
                // 7. Tentar também o total global do desenho.
                // -------------------------------------------------------------

                QtoReportData? totalDesenho =
                    null;

                string? erroGlobal =
                    null;

                try
                {
                    ed.WriteMessage(
                        "\nTentando gerar Takeoff global do desenho...");

                    string xmlGlobal =
                        Path.Combine(
                            pastaXml,
                            "TOTAL_DESENHO.xml");

                    totalDesenho =
                        GerarRelatorioNativoGlobal(
                            xmlGlobal,
                            corridorHandle,
                            nomesBaselinesCorridor);
                }
                catch (System.Exception exGlobal)
                {
                    erroGlobal =
                        exGlobal.Message;

                    ed.WriteMessage(
                        "\n[AVISO] O Takeoff global do desenho não pôde ser " +
                        "gerado automaticamente. A auditoria principal ainda " +
                        "será feita contra o total nativo do Alignment." +
                        "\nDetalhe: " + erroGlobal);
                }

                // -------------------------------------------------------------
                // 8. Relatórios de cada intervalo.
                // -------------------------------------------------------------

                List<QtoIntervalo> intervalos =
                    new List<QtoIntervalo>();

                Dictionary<string, QtoPayItem> somaIntervalos =
                    new Dictionary<string, QtoPayItem>(
                        StringComparer.OrdinalIgnoreCase);

                int indice = 0;
                int fallbackBaselineSemHandleIntervalos = 0;
                int semHandleDescartadosIntervalos = 0;
                int correcoesLinearesIntervalos = 0;
                double acrescimoCorrecoesLineares = 0.0;

                for (
                    double s0 = inicio;
                    s0 < fim - ToleranciaEstacao;
                    s0 += passo)
                {
                    double s1 =
                        Math.Min(
                            s0 + passo,
                            fim);

                    indice++;

                    ed.WriteMessage(
                        "\n  [" + indice + "/" + numeroIntervalos + "] " +
                        FormatarEstaca(s0) +
                        " -> " +
                        FormatarEstaca(s1));

                    string nomeXml =
                        indice.ToString("0000", CultureInfo.InvariantCulture) +
                        "_" +
                        EstacaoParaNome(s0) +
                        "_" +
                        EstacaoParaNome(s1) +
                        ".xml";

                    string caminhoXml =
                        Path.Combine(
                            pastaXml,
                            nomeXml);

                    QtoReportData relatorio =
                        GerarRelatorioNativoPorEstacao(
                            prAlignment.ObjectId,
                            s0,
                            s1,
                            caminhoXml,
                            corridorHandle,
                            nomesBaselinesCorridor);

                    fallbackBaselineSemHandleIntervalos +=
                        relatorio.ItensIncluidosPorBaselineSemHandle;

                    semHandleDescartadosIntervalos +=
                        relatorio.ItensSemHandleDescartados;

                    QtoIntervalo itemIntervalo =
                        new QtoIntervalo
                        {
                            Indice = indice,
                            Inicio = s0,
                            Fim = s1,
                            Xml = caminhoXml,
                            Relatorio = relatorio
                        };

                    intervalos.Add(
                        itemIntervalo);

                }

                // ---------------------------------------------------------
                // 8.1. Reparo conservador de falhas pontuais LT/RT em itens
                //      lineares de Corridor Feature Line.
                //
                // Diferente da v0.15, NÃO soma todas as Feature Lines
                // geométricas das Baselines, pois Corridors com Baselines
                // sobrepostas podem duplicar a contagem.
                //
                // Só repara um intervalo quando:
                // - o total nativo do Alignment mostra déficit na soma;
                // - o intervalo possui somente LT ou somente RT;
                // - os intervalos imediatamente anterior e posterior possuem
                //   LT e RT para o mesmo Pay Item;
                // - a soma dos candidatos é compatível com o déficit global.
                // O acréscimo é limitado exatamente ao déficit do Takeoff
                // nativo do Alignment.
                // ---------------------------------------------------------

                acrescimoCorrecoesLineares =
                    AplicarReparoLinearPorContinuidade(
                        intervalos,
                        totalAlignment,
                        ed,
                        out correcoesLinearesIntervalos);

                somaIntervalos.Clear();

                foreach (QtoIntervalo intervaloGerado in intervalos)
                {
                    SomarPayItems(
                        somaIntervalos,
                        intervaloGerado.Relatorio.PayItems);
                }

                ed.WriteMessage(
                    "\n[FILTRO INTERVALOS] fallback baseline sem Handle: " +
                    fallbackBaselineSemHandleIntervalos +
                    " | sem Handle descartados: " +
                    semHandleDescartadosIntervalos);

                ed.WriteMessage(
                    "\n[REPARO LINEAR LT/RT] correções automáticas: " +
                    correcoesLinearesIntervalos +
                    " | acréscimo total: " +
                    acrescimoCorrecoesLineares.ToString(
                        "0.###",
                        CultureInfo.GetCultureInfo("pt-BR")) +
                    " m");

                // -------------------------------------------------------------
                // 9. CSVs + TXT de auditoria.
                // -------------------------------------------------------------

                string csvIntervalos =
                    Path.Combine(
                        pastaSaida,
                        identificadorSaida + "__INTERVALOS.csv");

                string csvAuditoria =
                    Path.Combine(
                        pastaSaida,
                        identificadorSaida + "__AUDITORIA.csv");

                string txtResumo =
                    Path.Combine(
                        pastaSaida,
                        identificadorSaida + "__RESUMO.txt");

                GravarCsvIntervalos(
                    csvIntervalos,
                    intervalos);

                AuditoriaResultado auditoria =
                    GravarCsvAuditoria(
                        csvAuditoria,
                        somaIntervalos,
                        totalAlignment,
                        totalDesenho);

                GravarResumoEstudo(
                    txtResumo,
                    corridor.Name,
                    alignment.Name,
                    baselinesInfo,
                    inicio,
                    fim,
                    passo,
                    pastaSaida,
                    corridorHandle,
                    totalAlignment,
                    fallbackBaselineSemHandleIntervalos,
                    semHandleDescartadosIntervalos,
                    correcoesLinearesIntervalos,
                    acrescimoCorrecoesLineares,
                    auditoria,
                    erroGlobal);

                // -------------------------------------------------------------
                // 10. XLSX formatado - memória de cálculo.
                // -------------------------------------------------------------

                string xlsxMemoriaPadrao =
                    Path.Combine(
                        pastaSaida,
                        identificadorSaida + "__MEMORIA_CALCULO.xlsx");

                string xlsxMemoria =
                    ResolverCaminhoMemoriaExcel(
                        xlsxMemoriaPadrao,
                        ed);

                List<QtoExcelDetailRow> linhasDetalhadas =
                    MontarLinhasExcelDetalhadas(
                        intervalos);

                List<QtoExcelAuditRow> linhasAuditoria =
                    MontarLinhasExcelAuditoria(
                        somaIntervalos,
                        totalAlignment,
                        totalDesenho);

                // ---------------------------------------------------------
                // 10.1. Interface limpa para consolidação/orçamento.
                //
                // Não expõe Pay Item ID, Code/Fonte, XML, handle ou outros
                // identificadores internos do Civil 3D. O consolidador externo
                // usa Serviço + Unidade e os dados de identificação do trecho.
                // ---------------------------------------------------------

                string csvResumoOrcamento =
                    Path.Combine(
                        pastaSaida,
                        identificadorSaida + "__RESUMO_ORCAMENTO.csv");

                GravarCsvResumoOrcamento(
                    csvResumoOrcamento,
                    nomeDwg,
                    corridor.Name,
                    alignment.Name,
                    extensaoGeometrica,
                    FormatarEstaca(inicio),
                    FormatarEstaca(fim),
                    linhasAuditoria);

                QtoExcelContext excelContext =
                    new QtoExcelContext
                    {
                        Versao = Versao,
                        Dwg = string.IsNullOrWhiteSpace(db.Filename)
                            ? nomeDwg
                            : db.Filename,
                        Corredor = corridor.Name,
                        Alignment = alignment.Name,
                        AlignmentTipo = alignmentTipo,
                        AlignmentLayer = alignment.Layer,
                        AlignmentDescricao = alignmentDescricao,
                        EstacaInicial = FormatarEstaca(inicio),
                        EstacaFinal = FormatarEstaca(fim),
                        ExtensaoGeometrica = extensaoGeometrica,
                        AmplitudeEstaqueamento = Math.Abs(fim - inicio),
                        IntervaloMetros = passo,
                        ToleranciaPercentual = ToleranciaAuditoriaPercentual,
                        GeradoEm = DateTime.Now,
                        Baselines = baselinesInfo,
                        Detalhes = linhasDetalhadas,
                        Auditoria = linhasAuditoria,
                        Elementos = elementosAlinhamento,
                        CorridorHandle = corridorHandle,
                        ItensRelatorioTotal = totalAlignment.ItensDetalhados,
                        ItensDoCorridor = totalAlignment.ItensCorrespondentes,
                        ItensDescartados = totalAlignment.ItensDescartados,
                        HandlesEncontrados = totalAlignment.HandlesEncontrados,
                        CorrecoesLineares = correcoesLinearesIntervalos,
                        AcrescimoCorrecoesLineares = acrescimoCorrecoesLineares
                    };

                QtoExcelExporter.Exportar(
                    xlsxMemoria,
                    excelContext);

                // -------------------------------------------------------------
                // 11. Resultado em linha de comando.
                // -------------------------------------------------------------

                ed.WriteMessage(
                    "\n\n====================================================" +
                    "\n QTOESTUDO v0.16 - RESULTADO" +
                    "\n====================================================" +
                    "\nPay Items auditados: " + auditoria.TotalItens +
                    "\nOK (<= 5%): " + auditoria.Ok +
                    "\nDIVERGÊNCIA (> 5%): " + auditoria.Divergentes +
                    "\nSem referência: " + auditoria.SemReferencia +
                    "\nFiltro por Corridor: VALIDADO" +
                    "\nHandle: " + corridorHandle +
                    "\nItens usados: " + totalAlignment.ItensCorrespondentes +
                    " / " + totalAlignment.ItensDetalhados +
                    "\nCorreções lineares automáticas: " + correcoesLinearesIntervalos +
                    "\nAcréscimo linear total: " +
                    acrescimoCorrecoesLineares.ToString(
                        "0.###",
                        CultureInfo.GetCultureInfo("pt-BR")) +
                    " m" +
                    "\n" +
                    "\nFechamento principal: SOMA DOS INTERVALOS FILTRADOS" +
                    "\n                     x TAKEOFF NATIVO DO ALIGNMENT FILTRADO" +
                    (totalDesenho != null
                        ? "\nTambém foi gravada comparação x TAKEOFF GLOBAL DO DESENHO FILTRADO."
                        : "\nTakeoff global do desenho: indisponível nesta execução.") +
                    "\n" +
                    "\nSaída:" +
                    "\n  " + pastaSaida +
                    "\n" +
                    "\nArquivos principais:" +
                    "\n  " + Path.GetFileName(csvIntervalos) +
                    "\n  " + Path.GetFileName(csvAuditoria) +
                    "\n  " + Path.GetFileName(csvResumoOrcamento) +
                    "\n  " + Path.GetFileName(txtResumo) +
                    "\n  " + Path.GetFileName(xlsxMemoria) +
                    "\n  XML\\TOTAL_ALIGNMENT.xml" +
                    "\n  XML\\<cada intervalo>.xml" +
                    "\n====================================================\n");

                tr.Commit();
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage(
                    "\n\n====================================================" +
                    "\n QTOESTUDO - ERRO" +
                    "\n====================================================" +
                    "\n" + ex.Message +
                    "\nTipo: " + ex.GetType().FullName +
                    "\n====================================================\n");
            }
        }


        // =====================================================================
        // GERAÇÃO NATIVA QTO
        // =====================================================================

        private static QtoReportData GerarRelatorioNativoPorEstacao(
            ObjectId alignmentId,
            double inicio,
            double fim,
            string destinoXml,
            string corridorHandle,
            ISet<string> nomesBaselinesCorridor)
        {
            QTOGenerateDetail detalhe =
                new QTOGenerateDetail
                {
                    AlignmentId = alignmentId,
                    StartStation = inicio,
                    EndStation = fim,
                    ReportType = QTOReportType.DetailedReport
                };

            return GerarRelatorioNativo(
                detalhe,
                destinoXml,
                corridorHandle,
                nomesBaselinesCorridor);
        }


        private static QtoReportData GerarRelatorioNativoGlobal(
            string destinoXml,
            string corridorHandle,
            ISet<string> nomesBaselinesCorridor)
        {
            QTOGenerateDetail detalhe =
                new QTOGenerateDetail
                {
                    ReportExtent = QTOReportExtent.DrawingExtent,
                    ReportType = QTOReportType.DetailedReport
                };

            return GerarRelatorioNativo(
                detalhe,
                destinoXml,
                corridorHandle,
                nomesBaselinesCorridor);
        }


        private static QtoReportData GerarRelatorioNativo(
            QTOGenerateDetail detalhe,
            string destinoXml,
            string corridorHandle,
            ISet<string> nomesBaselinesCorridor)
        {
            string? pasta =
                Path.GetDirectoryName(destinoXml);

            if (!string.IsNullOrWhiteSpace(pasta))
            {
                Directory.CreateDirectory(pasta);
            }

            string reportFilePath =
                destinoXml;

            string[] generatedPayItemIds =
                Array.Empty<string>();

            bool ok =
                QTOUtility.GenerateXMLReport(
                    detalhe,
                    ref reportFilePath,
                    ref generatedPayItemIds);

            if (!ok)
            {
                throw new InvalidOperationException(
                    "QTOUtility.GenerateXMLReport retornou false.");
            }

            string origem;

            if (File.Exists(reportFilePath))
            {
                origem = reportFilePath;
            }
            else if (File.Exists(destinoXml))
            {
                origem = destinoXml;
            }
            else
            {
                throw new FileNotFoundException(
                    "O Civil 3D informou sucesso, mas o XML do QTO não foi localizado.",
                    reportFilePath);
            }

            if (!string.Equals(
                Path.GetFullPath(origem),
                Path.GetFullPath(destinoXml),
                StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(
                    origem,
                    destinoXml,
                    true);
            }

            return LerRelatorioQto(
                destinoXml,
                generatedPayItemIds,
                corridorHandle,
                nomesBaselinesCorridor);
        }


        // =====================================================================
        // LEITURA DO XML NATIVO
        // Namespace-agnostic: usa LocalName para suportar o XML do Civil.
        // =====================================================================

        private static QtoReportData LerRelatorioQto(
            string caminhoXml,
            string[] generatedPayItemIds,
            string corridorHandle,
            ISet<string> nomesBaselinesCorridor)
        {
            XDocument doc =
                XDocument.Load(
                    caminhoXml,
                    LoadOptions.None);

            string handleFiltro =
                NormalizarHandle(corridorHandle);

            Dictionary<string, QtoPayItem> itens =
                new Dictionary<string, QtoPayItem>(
                    StringComparer.OrdinalIgnoreCase);

            HashSet<string> handlesEncontrados =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            int itensDetalhados = 0;
            int itensCorrespondentes = 0;
            int itensIncluidosPorBaselineSemHandle = 0;
            int itensSemHandleDescartados = 0;

            IEnumerable<XElement> payNodes =
                doc
                    .Descendants()
                    .Where(
                        x => x.Name.LocalName == "DesignProjectPayItem");

            foreach (XElement node in payNodes)
            {
                string id =
                    ObterValorDescendente(
                        node,
                        "referencePayItemID");

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                string descricao =
                    ObterValorDescendente(
                        node,
                        "description");

                string unidade =
                    ObterValorDescendente(
                        node,
                        "unitOfMeasureID");

                // O Detailed Report do Civil 3D traz, sob cada Pay Item,
                // os elementos detalhados que participaram da quantidade.
                //
                // Regra principal de isolamento:
                //   1) se o item tem AutoCADObjectHandle, ele só entra quando o
                //      Handle corresponde ao Corridor selecionado;
                //   2) alguns itens lineares do próprio Corridor podem aparecer
                //      em relatórios fracionados SEM AutoCADObjectHandle. Nesses
                //      casos usamos o baselineName como fallback, mas somente se
                //      ele corresponder a uma Baseline/Alignment do Corridor
                //      selecionado;
                //   3) item sem Handle e sem baselineName reconhecido é descartado.
                //
                // Esse fallback foi acrescentado na v0.14 após um teste perder
                // aproximadamente 20 m de uma Corridor Feature Line em um único
                // intervalo, apesar de o Takeoff total do Alignment estar correto.
                List<XElement> detalhes =
                    node
                        .Descendants()
                        .Where(x => x.Name.LocalName == "referenceBaseline")
                        .SelectMany(
                            rb => rb
                                .Descendants()
                                .Where(x => x.Name.LocalName == "item"))
                        .Where(
                            x => x
                                .Descendants()
                                .Any(d => d.Name.LocalName == "quantity"))
                        .ToList();

                foreach (XElement detalhe in detalhes)
                {
                    string h =
                        NormalizarHandle(
                            ObterValorDescendente(
                                detalhe,
                                "AutoCADObjectHandle"));

                    if (!string.IsNullOrWhiteSpace(h))
                    {
                        handlesEncontrados.Add(h);
                    }
                }

                itensDetalhados +=
                    detalhes.Count;

                List<XElement> detalhesFiltrados =
                    new List<XElement>();

                foreach (XElement detalhe in detalhes)
                {
                    string h =
                        NormalizarHandle(
                            ObterValorDescendente(
                                detalhe,
                                "AutoCADObjectHandle"));

                    if (!string.IsNullOrWhiteSpace(h))
                    {
                        if (string.Equals(
                            h,
                            handleFiltro,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            detalhesFiltrados.Add(detalhe);
                        }

                        continue;
                    }

                    string baselineName =
                        ObterValorDescendente(
                            detalhe,
                            "baselineName");

                    if (BaselinePertenceAoCorridor(
                        baselineName,
                        nomesBaselinesCorridor))
                    {
                        detalhesFiltrados.Add(detalhe);
                        itensIncluidosPorBaselineSemHandle++;
                    }
                    else
                    {
                        itensSemHandleDescartados++;
                    }
                }

                itensCorrespondentes +=
                    detalhesFiltrados.Count;

                // Se o relatório não possuir itens detalhados reconhecíveis, não
                // reaproveitamos estimatedQuantity, pois isso poderia misturar
                // quantidades de outros objetos do desenho.
                if (detalhesFiltrados.Count == 0)
                    continue;

                double quantidade =
                    0.0;

                Dictionary<string, double> quantidadePorLado =
                    new Dictionary<string, double>(
                        StringComparer.OrdinalIgnoreCase);

                foreach (XElement detalhe in detalhesFiltrados)
                {
                    string quantidadeTexto =
                        ObterValorDescendente(
                            detalhe,
                            "quantity");

                    if (double.TryParse(
                        quantidadeTexto,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double q))
                    {
                        quantidade += q;

                        string lado =
                            ObterValorDescendente(
                                detalhe,
                                "side")
                            .Trim()
                            .ToUpperInvariant();

                        if (lado == "LT" || lado == "RT")
                        {
                            if (!quantidadePorLado.ContainsKey(lado))
                                quantidadePorLado[lado] = 0.0;

                            quantidadePorLado[lado] += q;
                        }
                    }
                }

                string codigoFonte =
                    ObterCodigosFonte(
                        detalhesFiltrados);

                if (!itens.TryGetValue(id, out QtoPayItem? item))
                {
                    item =
                        new QtoPayItem
                        {
                            Id = id,
                            Descricao = descricao,
                            Unidade = unidade,
                            CodigoFonte = codigoFonte,
                            Quantidade = 0.0
                        };

                    itens.Add(
                        id,
                        item);
                }

                item.Quantidade +=
                    quantidade;

                foreach (KeyValuePair<string, double> lado in quantidadePorLado)
                {
                    if (!item.QuantidadePorLado.ContainsKey(lado.Key))
                        item.QuantidadePorLado[lado.Key] = 0.0;

                    item.QuantidadePorLado[lado.Key] += lado.Value;
                }

                if (string.IsNullOrWhiteSpace(item.Descricao))
                    item.Descricao = descricao;

                if (string.IsNullOrWhiteSpace(item.Unidade))
                    item.Unidade = unidade;

                item.CodigoFonte =
                    MesclarCodigosFonte(
                        item.CodigoFonte,
                        codigoFonte);
            }

            return new QtoReportData
            {
                Xml = caminhoXml,
                PayItems = itens,
                GeneratedPayItemIds = generatedPayItemIds ?? Array.Empty<string>(),
                CorridorHandleFiltro = handleFiltro,
                ItensDetalhados = itensDetalhados,
                ItensCorrespondentes = itensCorrespondentes,
                ItensDescartados = Math.Max(0, itensDetalhados - itensCorrespondentes),
                ItensIncluidosPorBaselineSemHandle = itensIncluidosPorBaselineSemHandle,
                ItensSemHandleDescartados = itensSemHandleDescartados,
                HandlesEncontrados = handlesEncontrados
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }


        private static string NormalizarHandle(
            string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor))
                return string.Empty;

            string v =
                valor
                    .Trim()
                    .ToUpperInvariant();

            if (v.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
                v = v.Substring(2);

            // Normaliza zeros à esquerda quando o conteúdo é hexadecimal.
            if (ulong.TryParse(
                v,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out ulong numero))
            {
                return numero.ToString("X", CultureInfo.InvariantCulture);
            }

            return v;
        }


        private static bool BaselinePertenceAoCorridor(
            string? baselineName,
            ISet<string> nomesBaselinesCorridor)
        {
            if (string.IsNullOrWhiteSpace(baselineName))
                return false;

            string nome = baselineName.Trim();

            foreach (string candidato in nomesBaselinesCorridor)
            {
                if (string.Equals(
                    nome,
                    candidato?.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }


        private static string ObterValorDescendente(
            XElement pai,
            string localName)
        {
            XElement? el =
                pai
                    .Descendants()
                    .FirstOrDefault(
                        x => x.Name.LocalName == localName);

            return el?.Value?.Trim() ??
                string.Empty;
        }


        private static string ObterCodigosFonte(
            IEnumerable<XElement> detalhes)
        {
            IEnumerable<string> codigos =
                detalhes
                    .SelectMany(x => x.Descendants())
                    .Where(x => x.Name.LocalName == "remarks")
                    .Select(x => ExtrairCodigoFonte(x.Value))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

            return string.Join(", ", codigos);
        }


        private static string ExtrairCodigoFonte(
            string remarks)
        {
            if (string.IsNullOrWhiteSpace(remarks))
                return string.Empty;

            string texto = remarks.Trim();
            int idxCode = texto.IndexOf("Code:", StringComparison.OrdinalIgnoreCase);

            if (idxCode >= 0)
                texto = texto.Substring(idxCode + 5).Trim();

            int idxFormula = texto.IndexOf("(Formula:", StringComparison.OrdinalIgnoreCase);
            if (idxFormula >= 0)
                texto = texto.Substring(0, idxFormula).Trim();

            return texto.Trim(' ', ';', ',', '.');
        }


        private static string MesclarCodigosFonte(
            string atual,
            string novo)
        {
            IEnumerable<string> valores =
                (atual + "," + novo)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

            return string.Join(", ", valores);
        }


        // =====================================================================
        // SOMA / AUDITORIA
        // =====================================================================

        private static void SomarPayItems(
            Dictionary<string, QtoPayItem> destino,
            Dictionary<string, QtoPayItem> origem)
        {
            foreach (
                KeyValuePair<string, QtoPayItem> par
                in origem)
            {
                if (!destino.TryGetValue(
                    par.Key,
                    out QtoPayItem? atual))
                {
                    atual =
                        new QtoPayItem
                        {
                            Id = par.Value.Id,
                            Descricao = par.Value.Descricao,
                            Unidade = par.Value.Unidade,
                            CodigoFonte = par.Value.CodigoFonte,
                            Quantidade = 0.0
                        };

                    destino.Add(
                        par.Key,
                        atual);
                }

                atual.Quantidade +=
                    par.Value.Quantidade;

                atual.CodigoFonte =
                    MesclarCodigosFonte(
                        atual.CodigoFonte,
                        par.Value.CodigoFonte);
            }
        }


        private static double? CalcularDiferencaPercentual(
            double calculado,
            double? referencia)
        {
            if (!referencia.HasValue)
                return null;

            double refValor =
                referencia.Value;

            if (Math.Abs(refValor) <= 0.000000001)
            {
                return Math.Abs(calculado) <= 0.000000001
                    ? 0.0
                    : 100.0;
            }

            return
                Math.Abs(
                    calculado -
                    refValor) /
                Math.Abs(refValor) *
                100.0;
        }


        private static string StatusAuditoria(
            double? diferencaPercentual)
        {
            if (!diferencaPercentual.HasValue)
                return "SEM REFERÊNCIA";

            return diferencaPercentual.Value <=
                   ToleranciaAuditoriaPercentual
                ? "OK"
                : "DIVERGÊNCIA";
        }


        // =====================================================================
        // XLSX - LINHAS DETALHADAS / AUDITORIA
        // =====================================================================

        private static List<QtoExcelDetailRow> MontarLinhasExcelDetalhadas(
            List<QtoIntervalo> intervalos)
        {
            List<QtoExcelDetailRow> linhas =
                new List<QtoExcelDetailRow>();

            foreach (QtoIntervalo intervalo in intervalos)
            {
                string trecho =
                    FormatarEstaca(intervalo.Inicio) +
                    " – " +
                    FormatarEstaca(intervalo.Fim);

                foreach (
                    QtoPayItem item
                    in intervalo.Relatorio.PayItems.Values
                        .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
                {
                    string estacaInicial =
                        FormatarEstaca(intervalo.Inicio);

                    string estacaFinal =
                        FormatarEstaca(intervalo.Fim);

                    QtoExcelDetailRow linha =
                        new QtoExcelDetailRow
                        {
                            Trecho = trecho,
                            EstacaInicial = estacaInicial,
                            EstacaFinal = estacaFinal,
                            PayItem = item.Id,
                            Descricao = item.Descricao,
                            CodigoFonte = item.CodigoFonte,
                            Unidade = item.Unidade,
                            QuantidadeNativa = item.Quantidade,
                            Chave = trecho + "|" + item.Id
                        };

                    string unidade =
                        NormalizarUnidade(
                            item.Unidade);

                    if (unidade == "m2")
                    {
                        linha.Area = item.Quantidade;
                    }
                    else if (unidade == "m3")
                    {
                        linha.Volume = item.Quantidade;
                    }
                    else if (unidade == "m")
                    {
                        linha.Comprimento = item.Quantidade;
                    }

                    linhas.Add(
                        linha);
                }
            }

            return linhas;
        }


        private static List<QtoExcelAuditRow> MontarLinhasExcelAuditoria(
            Dictionary<string, QtoPayItem> somaIntervalos,
            QtoReportData totalAlignment,
            QtoReportData? totalDesenho)
        {
            HashSet<string> ids =
                new HashSet<string>(
                    somaIntervalos.Keys,
                    StringComparer.OrdinalIgnoreCase);

            ids.UnionWith(
                totalAlignment.PayItems.Keys);

            if (totalDesenho != null)
            {
                ids.UnionWith(
                    totalDesenho.PayItems.Keys);
            }

            List<QtoExcelAuditRow> linhas =
                new List<QtoExcelAuditRow>();

            foreach (
                string id
                in ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                somaIntervalos.TryGetValue(
                    id,
                    out QtoPayItem? somaItem);

                totalAlignment.PayItems.TryGetValue(
                    id,
                    out QtoPayItem? aliItem);

                QtoPayItem? desenhoItem =
                    null;

                totalDesenho?.PayItems.TryGetValue(
                    id,
                    out desenhoItem);

                double calculado =
                    somaItem?.Quantidade ?? 0.0;

                double? refAli =
                    aliItem?.Quantidade;

                double? refDesenho =
                    desenhoItem?.Quantidade;

                double? difPctAli =
                    CalcularDiferencaPercentual(
                        calculado,
                        refAli);

                double? difPctDesenho =
                    CalcularDiferencaPercentual(
                        calculado,
                        refDesenho);

                QtoPayItem? fonte =
                    somaItem ??
                    aliItem ??
                    desenhoItem;

                linhas.Add(
                    new QtoExcelAuditRow
                    {
                        PayItem = id,
                        Descricao = fonte?.Descricao ?? string.Empty,
                        Unidade = fonte?.Unidade ?? string.Empty,
                        SomaIntervalos = calculado,
                        TakeoffAlignment = refAli,
                        DiferencaAbsAlignment = refAli.HasValue
                            ? Math.Abs(calculado - refAli.Value)
                            : null,
                        DiferencaPctAlignment = difPctAli,
                        StatusAlignment = StatusAuditoria(difPctAli),
                        TakeoffDesenho = refDesenho,
                        DiferencaAbsDesenho = refDesenho.HasValue
                            ? Math.Abs(calculado - refDesenho.Value)
                            : null,
                        DiferencaPctDesenho = difPctDesenho,
                        StatusDesenho = StatusAuditoria(difPctDesenho)
                    });
            }

            return linhas;
        }


        private static string NormalizarUnidade(
            string unidade)
        {
            string u =
                (unidade ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant()
                    .Replace("²", "2")
                    .Replace("³", "3")
                    .Replace("^", string.Empty)
                    .Replace(" ", string.Empty);

            if (u == "m2" || u == "sqm")
                return "m2";

            if (u == "m3" || u == "cum")
                return "m3";

            if (u == "m" || u == "ml" || u == "lm")
                return "m";

            return u;
        }


        // =====================================================================
        // INFORMAÇÕES GEOMÉTRICAS DO ALIGNMENT
        // =====================================================================

        private static List<QtoAlignmentElementInfo> ObterElementosAlinhamento(
            Alignment alignment)
        {
            List<QtoAlignmentElementInfo> resultado =
                new List<QtoAlignmentElementInfo>();

            int ordem = 0;

            foreach (AlignmentEntity entidade in alignment.Entities)
            {
                ordem++;

                string tipoNativo =
                    entidade.EntityType.ToString();

                double? startStation =
                    ObterDoublePropriedade(
                        entidade,
                        "StartStation");

                double? endStation =
                    ObterDoublePropriedade(
                        entidade,
                        "EndStation");

                double? length =
                    ObterDoublePropriedade(
                        entidade,
                        "Length");

                double? radius =
                    ObterDoublePropriedade(
                        entidade,
                        "Radius");

                // Algumas entidades da API não expõem Length. Para a tabela
                // de memória de cálculo, a diferença de estaqueamento é um
                // fallback consistente para o comprimento horizontal do
                // elemento ao longo do Alignment.
                if (!length.HasValue && startStation.HasValue && endStation.HasValue)
                {
                    length = Math.Abs(endStation.Value - startStation.Value);
                }

                List<string> detalhes =
                    new List<string>();

                double? startRadius =
                    ObterDoublePropriedade(
                        entidade,
                        "StartRadius");

                double? endRadius =
                    ObterDoublePropriedade(
                        entidade,
                        "EndRadius");

                double? a =
                    ObterDoublePropriedade(
                        entidade,
                        "A");

                bool? clockwise =
                    ObterBoolPropriedade(
                        entidade,
                        "Clockwise");

                string constraint1 =
                    ObterStringPropriedade(
                        entidade,
                        "Constraint1");

                string constraint2 =
                    ObterStringPropriedade(
                        entidade,
                        "Constraint2");

                int? subEntityCount =
                    ObterIntPropriedade(
                        entidade,
                        "SubEntityCount");

                if (startRadius.HasValue)
                    detalhes.Add("R inicial=" + startRadius.Value.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR")) + " m");

                if (endRadius.HasValue)
                    detalhes.Add("R final=" + endRadius.Value.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR")) + " m");

                if (a.HasValue)
                    detalhes.Add("A=" + a.Value.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR")));

                if (clockwise.HasValue)
                    detalhes.Add(clockwise.Value ? "sentido horário" : "sentido anti-horário");

                if (!string.IsNullOrWhiteSpace(constraint1))
                    detalhes.Add("restrição 1=" + constraint1);

                if (!string.IsNullOrWhiteSpace(constraint2))
                    detalhes.Add("restrição 2=" + constraint2);

                if (subEntityCount.HasValue && subEntityCount.Value > 1)
                    detalhes.Add("subentidades=" + subEntityCount.Value.ToString(CultureInfo.InvariantCulture));

                resultado.Add(
                    new QtoAlignmentElementInfo
                    {
                        Ordem = ordem,
                        Tipo = TraduzirTipoEntidade(tipoNativo),
                        TipoNativo = tipoNativo,
                        EstacaInicial = startStation,
                        EstacaFinal = endStation,
                        EstacaInicialFormatada = startStation.HasValue
                            ? FormatarEstaca(startStation.Value)
                            : string.Empty,
                        EstacaFinalFormatada = endStation.HasValue
                            ? FormatarEstaca(endStation.Value)
                            : string.Empty,
                        Extensao = length,
                        Raio = radius,
                        Detalhes = string.Join(" | ", detalhes)
                    });
            }

            resultado = resultado
                .OrderBy(x => x.EstacaInicial ?? double.MaxValue)
                .ThenBy(x => x.EstacaFinal ?? double.MaxValue)
                .ToList();

            for (int i = 0; i < resultado.Count; i++)
            {
                resultado[i].Ordem = i + 1;
            }

            return resultado;
        }


        private static string TraduzirTipoEntidade(
            string tipoNativo)
        {
            if (tipoNativo.Equals("Line", StringComparison.OrdinalIgnoreCase))
                return "Tangente";

            if (tipoNativo.Equals("Arc", StringComparison.OrdinalIgnoreCase))
                return "Curva circular";

            if (tipoNativo.Equals("Spiral", StringComparison.OrdinalIgnoreCase))
                return "Espiral";

            return tipoNativo;
        }


        private static object? ObterValorPropriedade(
            object objeto,
            string nome)
        {
            try
            {
                PropertyInfo? propriedade =
                    objeto
                        .GetType()
                        .GetProperty(
                            nome,
                            BindingFlags.Public |
                            BindingFlags.Instance);

                return propriedade?.GetValue(objeto);
            }
            catch
            {
                return null;
            }
        }


        private static double? ObterDoublePropriedade(
            object objeto,
            string nome)
        {
            object? valor =
                ObterValorPropriedade(
                    objeto,
                    nome);

            if (valor == null)
                return null;

            try
            {
                return Convert.ToDouble(
                    valor,
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }


        private static int? ObterIntPropriedade(
            object objeto,
            string nome)
        {
            object? valor =
                ObterValorPropriedade(
                    objeto,
                    nome);

            if (valor == null)
                return null;

            try
            {
                return Convert.ToInt32(
                    valor,
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }


        private static bool? ObterBoolPropriedade(
            object objeto,
            string nome)
        {
            object? valor =
                ObterValorPropriedade(
                    objeto,
                    nome);

            if (valor is bool b)
                return b;

            return null;
        }


        private static string ObterStringPropriedade(
            object objeto,
            string nome)
        {
            object? valor =
                ObterValorPropriedade(
                    objeto,
                    nome);

            return valor?.ToString() ?? string.Empty;
        }


        // =====================================================================
        // CSV INTERVALOS
        // =====================================================================

        private static void GravarCsvIntervalos(
            string caminho,
            List<QtoIntervalo> intervalos)
        {
            CultureInfo ptBr =
                CultureInfo.GetCultureInfo(
                    "pt-BR");

            StringBuilder sb =
                new StringBuilder();

            sb.AppendLine(
                "Indice;Trecho;Inicio;Fim;Pay Item;Descrição;Code/Fonte;Unidade;Quantidade;XML");

            foreach (QtoIntervalo intervalo in intervalos)
            {
                foreach (
                    QtoPayItem item
                    in intervalo.Relatorio.PayItems.Values
                        .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
                {
                    string trecho =
                        FormatarEstaca(intervalo.Inicio) +
                        " -> " +
                        FormatarEstaca(intervalo.Fim);

                    sb.Append(intervalo.Indice.ToString(CultureInfo.InvariantCulture));
                    sb.Append(';');
                    sb.Append(Csv(trecho));
                    sb.Append(';');
                    sb.Append(intervalo.Inicio.ToString("0.###", ptBr));
                    sb.Append(';');
                    sb.Append(intervalo.Fim.ToString("0.###", ptBr));
                    sb.Append(';');
                    sb.Append(Csv(item.Id));
                    sb.Append(';');
                    sb.Append(Csv(item.Descricao));
                    sb.Append(';');
                    sb.Append(Csv(item.CodigoFonte));
                    sb.Append(';');
                    sb.Append(Csv(item.Unidade));
                    sb.Append(';');
                    sb.Append(item.Quantidade.ToString("0.######", ptBr));
                    sb.Append(';');
                    sb.Append(Csv(Path.GetFileName(intervalo.Xml)));
                    sb.AppendLine();
                }
            }

            File.WriteAllText(
                caminho,
                sb.ToString(),
                new UTF8Encoding(true));
        }


        // =====================================================================
        // CSV AUDITORIA
        // =====================================================================

        private static AuditoriaResultado GravarCsvAuditoria(
            string caminho,
            Dictionary<string, QtoPayItem> somaIntervalos,
            QtoReportData totalAlignment,
            QtoReportData? totalDesenho)
        {
            CultureInfo ptBr =
                CultureInfo.GetCultureInfo(
                    "pt-BR");

            HashSet<string> ids =
                new HashSet<string>(
                    somaIntervalos.Keys,
                    StringComparer.OrdinalIgnoreCase);

            ids.UnionWith(
                totalAlignment.PayItems.Keys);

            if (totalDesenho != null)
            {
                ids.UnionWith(
                    totalDesenho.PayItems.Keys);
            }

            StringBuilder sb =
                new StringBuilder();

            sb.AppendLine(
                "Pay Item;Descrição;Unidade;Soma Intervalos;Takeoff Alignment;Dif. Abs Alignment;Dif. % Alignment;Status Alignment;Takeoff Desenho;Dif. Abs Desenho;Dif. % Desenho;Status Desenho");

            AuditoriaResultado resultado =
                new AuditoriaResultado();

            foreach (
                string id
                in ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                somaIntervalos.TryGetValue(
                    id,
                    out QtoPayItem? somaItem);

                totalAlignment.PayItems.TryGetValue(
                    id,
                    out QtoPayItem? aliItem);

                QtoPayItem? desenhoItem =
                    null;

                totalDesenho?.PayItems.TryGetValue(
                    id,
                    out desenhoItem);

                double calculado =
                    somaItem?.Quantidade ?? 0.0;

                double? refAli =
                    aliItem?.Quantidade;

                double? refDesenho =
                    desenhoItem?.Quantidade;

                double? difPctAli =
                    CalcularDiferencaPercentual(
                        calculado,
                        refAli);

                double? difPctDesenho =
                    CalcularDiferencaPercentual(
                        calculado,
                        refDesenho);

                string statusAli =
                    StatusAuditoria(
                        difPctAli);

                string statusDesenho =
                    StatusAuditoria(
                        difPctDesenho);

                QtoPayItem? fonte =
                    somaItem ??
                    aliItem ??
                    desenhoItem;

                sb.Append(Csv(id));
                sb.Append(';');
                sb.Append(Csv(fonte?.Descricao ?? string.Empty));
                sb.Append(';');
                sb.Append(Csv(fonte?.Unidade ?? string.Empty));
                sb.Append(';');
                sb.Append(calculado.ToString("0.######", ptBr));
                sb.Append(';');
                sb.Append(refAli.HasValue ? refAli.Value.ToString("0.######", ptBr) : string.Empty);
                sb.Append(';');
                sb.Append(refAli.HasValue ? Math.Abs(calculado - refAli.Value).ToString("0.######", ptBr) : string.Empty);
                sb.Append(';');
                sb.Append(difPctAli.HasValue ? difPctAli.Value.ToString("0.###", ptBr) : string.Empty);
                sb.Append(';');
                sb.Append(Csv(statusAli));
                sb.Append(';');
                sb.Append(refDesenho.HasValue ? refDesenho.Value.ToString("0.######", ptBr) : string.Empty);
                sb.Append(';');
                sb.Append(refDesenho.HasValue ? Math.Abs(calculado - refDesenho.Value).ToString("0.######", ptBr) : string.Empty);
                sb.Append(';');
                sb.Append(difPctDesenho.HasValue ? difPctDesenho.Value.ToString("0.###", ptBr) : string.Empty);
                sb.Append(';');
                sb.Append(Csv(statusDesenho));
                sb.AppendLine();

                resultado.TotalItens++;

                // A classificação principal é contra o total do Alignment,
                // porque usa exatamente o mesmo AlignmentId dos intervalos.
                if (statusAli == "OK")
                {
                    resultado.Ok++;
                }
                else if (statusAli == "DIVERGÊNCIA")
                {
                    resultado.Divergentes++;
                }
                else
                {
                    resultado.SemReferencia++;
                }
            }

            File.WriteAllText(
                caminho,
                sb.ToString(),
                new UTF8Encoding(true));

            return resultado;
        }


        // =====================================================================
        // CSV LIMPO PARA CONSOLIDAÇÃO / ORÇAMENTO
        //
        // Este arquivo é deliberadamente desacoplado da linguagem interna do
        // Civil 3D. Não contém Pay Item ID, Code/Fonte, XML, handle etc.
        // Ele serve como contrato simples para o QTOConsolidador externo.
        // =====================================================================

        private static void GravarCsvResumoOrcamento(
            string caminho,
            string dwg,
            string corredor,
            string eixo,
            double extensaoEixo,
            string estacaInicial,
            string estacaFinal,
            List<QtoExcelAuditRow> linhas)
        {
            CultureInfo ptBr =
                CultureInfo.GetCultureInfo(
                    "pt-BR");

            StringBuilder sb =
                new StringBuilder();

            sb.AppendLine(
                "DWG;Corredor;Eixo;Extensão Eixo (m);Estaca Inicial;Estaca Final;Serviço;Unidade;Quantidade;Diferença %;Status");

            foreach (
                QtoExcelAuditRow linha
                in linhas.OrderBy(
                    x => x.Descricao,
                    StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(Csv(dwg));
                sb.Append(';');
                sb.Append(Csv(corredor));
                sb.Append(';');
                sb.Append(Csv(eixo));
                sb.Append(';');
                sb.Append(extensaoEixo.ToString("0.###", ptBr));
                sb.Append(';');
                sb.Append(Csv(estacaInicial));
                sb.Append(';');
                sb.Append(Csv(estacaFinal));
                sb.Append(';');
                sb.Append(Csv(linha.Descricao));
                sb.Append(';');
                sb.Append(Csv(linha.Unidade));
                sb.Append(';');
                sb.Append(linha.SomaIntervalos.ToString("0.######", ptBr));
                sb.Append(';');
                sb.Append(
                    linha.DiferencaPctAlignment.HasValue
                        ? linha.DiferencaPctAlignment.Value.ToString("0.###", ptBr)
                        : string.Empty);
                sb.Append(';');
                sb.Append(Csv(linha.StatusAlignment));
                sb.AppendLine();
            }

            File.WriteAllText(
                caminho,
                sb.ToString(),
                new UTF8Encoding(true));
        }


        // =====================================================================
        // RESUMO TXT
        // =====================================================================

        private static void GravarResumoEstudo(
            string caminho,
            string corredor,
            string alignment,
            List<string> baselines,
            double inicio,
            double fim,
            double passo,
            string pastaSaida,
            string corridorHandle,
            QtoReportData filtroTotalAlignment,
            int fallbackBaselineSemHandleIntervalos,
            int semHandleDescartadosIntervalos,
            int correcoesLinearesIntervalos,
            double acrescimoCorrecoesLineares,
            AuditoriaResultado auditoria,
            string? erroGlobal)
        {
            StringBuilder sb =
                new StringBuilder();

            sb.AppendLine("QTO v0.16 - MEMÓRIA DE CÁLCULO / AUDITORIA");
            sb.AppendLine("=================================================");
            sb.AppendLine("Corredor: " + corredor);
            sb.AppendLine("Alignment de referência: " + alignment);
            sb.AppendLine("Início: " + FormatarEstaca(inicio));
            sb.AppendLine("Fim: " + FormatarEstaca(fim));
            sb.AppendLine("Intervalo: " + passo.ToString("0.###", CultureInfo.InvariantCulture) + " m");
            sb.AppendLine("Tolerância: 5%");
            sb.AppendLine("Formato das estacas: padrão brasileiro de 20 m (ex.: 1+0.00).");
            sb.AppendLine("Dimensão: nativa do QTOUtility; total e intervalos gerados na mesma execução.");
            sb.AppendLine();
            sb.AppendLine("MOTOR / ABRANGÊNCIA");
            sb.AppendLine("-------------------");
            sb.AppendLine("O relatório é filtrado por AlignmentId + StartStation + EndStation.");
            sb.AppendLine("A opção de abrangência lateral foi removida porque AlignmentOffsetId não");
            sb.AppendLine("oferece filtro lateral funcional na API QTO 2026 e não alterava o cálculo.");
            sb.AppendLine();
            sb.AppendLine("FILTRO POR CORRIDOR");
            sb.AppendLine("-------------------");
            sb.AppendLine("Handle do Corridor selecionado: " + corridorHandle);
            sb.AppendLine("Itens detalhados no TOTAL_ALIGNMENT: " + filtroTotalAlignment.ItensDetalhados);
            sb.AppendLine("Itens do Corridor selecionado: " + filtroTotalAlignment.ItensCorrespondentes);
            sb.AppendLine("Itens incluídos por baselineName sem Handle: " + filtroTotalAlignment.ItensIncluidosPorBaselineSemHandle);
            sb.AppendLine("Itens sem Handle descartados no TOTAL_ALIGNMENT: " + filtroTotalAlignment.ItensSemHandleDescartados);
            sb.AppendLine("Itens descartados de outros objetos/corredores: " + filtroTotalAlignment.ItensDescartados);
            sb.AppendLine("Fallback baselineName usado nos intervalos: " + fallbackBaselineSemHandleIntervalos);
            sb.AppendLine("Itens sem Handle descartados nos intervalos: " + semHandleDescartadosIntervalos);
            sb.AppendLine("Handles encontrados: " +
                (filtroTotalAlignment.HandlesEncontrados.Count == 0
                    ? "(nenhum)"
                    : string.Join(", ", filtroTotalAlignment.HandlesEncontrados)));
            sb.AppendLine();
            sb.AppendLine("REPARO LINEAR POR CONTINUIDADE LT/RT");
            sb.AppendLine("-------------------------------------------");
            sb.AppendLine("Correções automáticas aplicadas: " + correcoesLinearesIntervalos);
            sb.AppendLine("Acréscimo total por correção: " +
                acrescimoCorrecoesLineares.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR")) +
                " m");
            sb.AppendLine("Critério: só repara falha pontual de LT/RT cercada por intervalos com os dois lados e quando os candidatos explicam o déficit do total nativo do Alignment.");
            sb.AppendLine();
            sb.AppendLine("BASELINES ENCONTRADAS NO CORRIDOR");
            sb.AppendLine("--------------------------------");

            foreach (string linha in baselines)
            {
                sb.AppendLine("- " + linha);
            }

            sb.AppendLine();
            sb.AppendLine("AUDITORIA PRINCIPAL");
            sb.AppendLine("-------------------");
            sb.AppendLine("Pay Items: " + auditoria.TotalItens);
            sb.AppendLine("OK <= 5%: " + auditoria.Ok);
            sb.AppendLine("DIVERGÊNCIA > 5%: " + auditoria.Divergentes);
            sb.AppendLine("Sem referência: " + auditoria.SemReferencia);

            if (!string.IsNullOrWhiteSpace(erroGlobal))
            {
                sb.AppendLine();
                sb.AppendLine("TAKEOFF GLOBAL DO DESENHO NÃO GERADO");
                sb.AppendLine(erroGlobal);
            }

            sb.AppendLine();
            sb.AppendLine("Pasta: " + pastaSaida);

            File.WriteAllText(
                caminho,
                sb.ToString(),
                new UTF8Encoding(true));
        }


        // =====================================================================
        // CAMINHO DA MEMÓRIA XLSX
        //
        // Se a memória está aberta no Excel, o Windows bloqueia a sobrescrita.
        // A v0.16 preserva o arquivo aberto e grava uma nova cópia atualizada,
        // evitando perder toda a execução do QTO no final.
        // =====================================================================

        private static string ResolverCaminhoMemoriaExcel(
            string caminhoPadrao,
            Editor ed)
        {
            if (!ArquivoEstaBloqueado(caminhoPadrao))
                return caminhoPadrao;

            string pasta =
                Path.GetDirectoryName(caminhoPadrao) ??
                string.Empty;

            string nome =
                Path.GetFileNameWithoutExtension(caminhoPadrao);

            string extensao =
                Path.GetExtension(caminhoPadrao);

            string sufixo =
                "__ATUALIZADA_" +
                DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture);

            string alternativo =
                Path.Combine(
                    pasta,
                    nome +
                    sufixo +
                    extensao);

            ed.WriteMessage(
                "\n[AVISO] A memória XLSX padrão está aberta ou bloqueada por outro processo." +
                "\n        O cálculo continuará normalmente." +
                "\n        Nova memória será salva em:" +
                "\n        " + alternativo);

            return alternativo;
        }


        private static bool ArquivoEstaBloqueado(
            string caminho)
        {
            if (!File.Exists(caminho))
                return false;

            try
            {
                using FileStream stream =
                    new FileStream(
                        caminho,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None);

                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }


        private static string Csv(
            string valor)
        {
            string v =
                valor ??
                string.Empty;

            if (
                v.Contains(';') ||
                v.Contains('"') ||
                v.Contains('\n') ||
                v.Contains('\r'))
            {
                return "\"" +
                       v.Replace("\"", "\"\"") +
                       "\"";
            }

            return v;
        }


        private static string LimparNomeArquivo(
            string valor)
        {
            char[] invalidos =
                Path.GetInvalidFileNameChars();

            StringBuilder sb =
                new StringBuilder();

            foreach (char c in valor)
            {
                sb.Append(
                    invalidos.Contains(c)
                        ? '_'
                        : c);
            }

            return sb.ToString();
        }


        private static string EstacaoParaNome(
            double station)
        {
            string s =
                station.ToString(
                    "0.000",
                    CultureInfo.InvariantCulture);

            return s
                .Replace("-", "M")
                .Replace(".", "p");
        }

        // =====================================================================
        // COLETAR LINKS DE UM CODE
        // =====================================================================

        private static SortedDictionary<double, List<Link2D>>
            ColetarLinksPorEstacao(
                Baseline baseline,
                string code,
                double inicio,
                double fim,
                Editor ed)
        {
            SortedDictionary<double, List<Link2D>> resultado =
                new SortedDictionary<double, List<Link2D>>();


            foreach (
                BaselineRegion region
                in baseline.BaselineRegions)
            {
                foreach (
                    AppliedAssembly assembly
                    in region.AppliedAssemblies)
                {
                    double? estacaoAssembly =
                        ObterEstacaoAssembly(
                            assembly);


                    if (!estacaoAssembly.HasValue)
                        continue;


                    double estacao =
                        estacaoAssembly.Value;


                    if (
                        estacao < inicio - ToleranciaEstacao ||
                        estacao > fim + ToleranciaEstacao)
                    {
                        continue;
                    }


                    CalculatedLinkCollection links;


                    try
                    {
                        links =
                            assembly.GetLinksByCode(
                                code);
                    }
                    catch
                    {
                        continue;
                    }


                    List<Link2D> encontrados =
                        new List<Link2D>();


                    foreach (
                        CalculatedLink link
                        in links)
                    {
                        List<double> offsets =
                            new List<double>();


                        foreach (
                            CalculatedPoint ponto
                            in link.CalculatedPoints)
                        {
                            double offset =
                                ponto
                                    .StationOffsetElevationToBaseline
                                    .Y;


                            offsets.Add(
                                offset);
                        }


                        if (offsets.Count < 2)
                            continue;


                        double offset1 =
                            offsets.Min();


                        double offset2 =
                            offsets.Max();


                        // Link degenerado / vertical.
                        // Não possui largura útil em planta.

                        if (
                            Math.Abs(
                                offset2 -
                                offset1) <
                            ToleranciaOffset)
                        {
                            continue;
                        }


                        encontrados.Add(
                            new Link2D
                            {
                                Offset1 =
                                    offset1,

                                Offset2 =
                                    offset2,

                                OffsetMedio =
                                    (offset1 + offset2) / 2.0
                            });
                    }


                    if (encontrados.Count == 0)
                        continue;


                    // =========================================================
                    // ORDENAR TRANSVERSALMENTE
                    //
                    // Offset negativo -> esquerda
                    // Offset positivo -> direita
                    // =========================================================

                    encontrados =
                        encontrados
                            .OrderBy(
                                x =>
                                    x.OffsetMedio)
                            .ToList();


                    double chave =
                        Math.Round(
                            estacao,
                            6);


                    if (
                        !resultado.ContainsKey(
                            chave))
                    {
                        resultado.Add(
                            chave,
                            encontrados);
                    }
                    else
                    {
                        // =====================================================
                        // Limite de BaselineRegions:
                        //
                        // uma mesma estação pode aparecer duas vezes.
                        //
                        // Neste primeiro algoritmo mantemos a seção que contém
                        // maior quantidade de links daquele code.
                        // =====================================================

                        if (
                            encontrados.Count >
                            resultado[chave].Count)
                        {
                            resultado[chave] =
                                encontrados;
                        }
                    }
                }
            }


            ed.WriteMessage(
                "\n\nLinks '" +
                code +
                "' encontrados nas estações:");


            foreach (
                KeyValuePair<double, List<Link2D>> secao
                in resultado)
            {
                ed.WriteMessage(
                    "\n  " +
                    FormatarEstaca(
                        secao.Key) +
                    " -> " +
                    secao.Value.Count +
                    " links");
            }


            return resultado;
        }


        // =====================================================================
        // CALCULAR ÁREA EM PLANTA ENTRE LINKS
        // =====================================================================

        private static double CalcularAreaLinks(
            SortedDictionary<double, List<Link2D>> secoes,
            Alignment alignment,
            Editor ed)
        {
            if (secoes.Count < 2)
                return 0.0;


            List<double> estacoes =
                secoes.Keys.ToList();


            double total =
                0.0;


            ed.WriteMessage(
                "\n\nÁreas parciais de Regularizaçao:");


            for (
                int i = 0;
                i < estacoes.Count - 1;
                i++)
            {
                double s0 =
                    estacoes[i];


                double s1 =
                    estacoes[i + 1];


                List<Link2D> links0 =
                    secoes[s0];


                List<Link2D> links1 =
                    secoes[s1];


                if (
                    links0.Count !=
                    links1.Count)
                {
                    ed.WriteMessage(
                        "\n  [AVISO] " +
                        FormatarEstaca(s0) +
                        " -> " +
                        FormatarEstaca(s1) +
                        ": quantidade de links mudou de " +
                        links0.Count +
                        " para " +
                        links1.Count +
                        "." +
                        "\n          Intervalo não calculado nesta versão.");


                    continue;
                }


                double areaIntervalo =
                    0.0;


                for (
                    int j = 0;
                    j < links0.Count;
                    j++)
                {
                    Link2D a =
                        links0[j];


                    Link2D b =
                        links1[j];


                    XY p1 =
                        ObterXY(
                            alignment,
                            s0,
                            a.Offset1);


                    XY p2 =
                        ObterXY(
                            alignment,
                            s0,
                            a.Offset2);


                    XY p3 =
                        ObterXY(
                            alignment,
                            s1,
                            b.Offset2);


                    XY p4 =
                        ObterXY(
                            alignment,
                            s1,
                            b.Offset1);


                    double area =
                        AreaPoligono(
                            p1,
                            p2,
                            p3,
                            p4);


                    areaIntervalo +=
                        area;
                }


                total +=
                    areaIntervalo;


                ed.WriteMessage(
                    "\n  " +
                    FormatarEstaca(
                        s0) +
                    " -> " +
                    FormatarEstaca(
                        s1) +
                    " = " +
                    areaIntervalo.ToString(
                        "0.000000",
                        CultureInfo.InvariantCulture) +
                    " m²");
            }


            return total;
        }


        // =====================================================================
        // CORRIDOR FEATURE LINE
        //
        // Calcula quantidade LINEAR diretamente da feature line criada
        // pelo próprio Corridor a partir do Point Code.
        // =====================================================================

        private static double CalcularComprimentoFeatureLine2D(
            Baseline baseline,
            string code,
            double inicio,
            double fim,
            Editor ed)
        {
            double total =
                0.0;


            int featureLinesEncontradas =
                0;


            ed.WriteMessage(
                "\n\nFeature Lines com Code '" +
                code +
                "':");


            // ================================================================
            // MAIN BASELINE FEATURE LINES
            // ================================================================

            BaselineFeatureLines baselineFeatureLines =
                baseline.MainBaselineFeatureLines;


            FeatureLineCollectionMap mapa =
                baselineFeatureLines.FeatureLineCollectionMap;


            foreach (
                FeatureLineCollection colecao
                in mapa)
            {
                foreach (
                    CorridorFeatureLine featureLine
                    in colecao)
                {
                    if (
                        !string.Equals(
                            featureLine.CodeName,
                            code,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }


                    featureLinesEncontradas++;


                    // =========================================================
                    // COPIAR OS PONTOS PARA UMA LISTA
                    // =========================================================

                    List<FeatureLinePoint> pontos =
                        new List<FeatureLinePoint>();


                    foreach (
                        FeatureLinePoint ponto
                        in featureLine.FeatureLinePoints)
                    {
                        pontos.Add(
                            ponto);
                    }


                    pontos =
                        pontos
                            .OrderBy(
                                p =>
                                    p.Station)
                            .ToList();


                    ed.WriteMessage(
                        "\n\n  Feature Line #" +
                        featureLinesEncontradas +
                        " | Code=" +
                        featureLine.CodeName +
                        " | pontos=" +
                        pontos.Count);


                    if (pontos.Count == 0)
                        continue;


                    ed.WriteMessage(
                        "\n    início = " +
                        FormatarEstaca(
                            pontos.First().Station) +
                        " | offset=" +
                        pontos.First().Offset.ToString(
                            "0.000",
                            CultureInfo.InvariantCulture));


                    ed.WriteMessage(
                        "\n    fim    = " +
                        FormatarEstaca(
                            pontos.Last().Station) +
                        " | offset=" +
                        pontos.Last().Offset.ToString(
                            "0.000",
                            CultureInfo.InvariantCulture));


                    if (pontos.Count < 2)
                    {
                        ed.WriteMessage(
                            "\n    [AVISO] Feature Line possui menos de 2 pontos.");

                        continue;
                    }


                    double subtotal =
                        0.0;


                    // =========================================================
                    // PERCORRER SEGMENTOS DA FEATURE LINE
                    // =========================================================

                    for (
                        int i = 0;
                        i < pontos.Count - 1;
                        i++)
                    {
                        FeatureLinePoint p0 =
                            pontos[i];


                        FeatureLinePoint p1 =
                            pontos[i + 1];


                        double s0 =
                            p0.Station;


                        double s1 =
                            p1.Station;


                        // =====================================================
                        // Segurança para ordem ou pontos repetidos
                        // =====================================================

                        if (
                            s1 <=
                            s0 +
                            ToleranciaEstacao)
                        {
                            continue;
                        }


                        // =====================================================
                        // Segmento totalmente antes do intervalo
                        // =====================================================

                        if (
                            s1 <=
                            inicio +
                            ToleranciaEstacao)
                        {
                            continue;
                        }


                        // =====================================================
                        // Segmento totalmente depois do intervalo
                        // =====================================================

                        if (
                            s0 >=
                            fim -
                            ToleranciaEstacao)
                        {
                            continue;
                        }


                        // =====================================================
                        // INTERSEÇÃO DO SEGMENTO COM O TRECHO SOLICITADO
                        // =====================================================

                        double recorteInicio =
                            Math.Max(
                                s0,
                                inicio);


                        double recorteFim =
                            Math.Min(
                                s1,
                                fim);


                        if (
                            recorteFim <=
                            recorteInicio +
                            ToleranciaEstacao)
                        {
                            continue;
                        }


                        // =====================================================
                        // INTERPOLAR XY NOS LIMITES
                        //
                        // IMPORTANTE:
                        // aqui Z é deliberadamente ignorado.
                        // =====================================================

                        XY xyInicio =
                            InterpolarFeatureLineXY(
                                p0,
                                p1,
                                recorteInicio);


                        XY xyFim =
                            InterpolarFeatureLineXY(
                                p0,
                                p1,
                                recorteFim);


                        double comprimento =
                            Distancia(
                                xyInicio,
                                xyFim);


                        subtotal +=
                            comprimento;


                        ed.WriteMessage(
                            "\n    " +
                            FormatarEstaca(
                                recorteInicio) +
                            " -> " +
                            FormatarEstaca(
                                recorteFim) +
                            " = " +
                            comprimento.ToString(
                                "0.000000",
                                CultureInfo.InvariantCulture) +
                            " m");
                    }


                    total +=
                        subtotal;


                    ed.WriteMessage(
                        "\n    Subtotal Feature Line = " +
                        subtotal.ToString(
                            "0.000000",
                            CultureInfo.InvariantCulture) +
                        " m");
                }
            }


            if (featureLinesEncontradas == 0)
            {
                ed.WriteMessage(
                    "\n  [AVISO] Nenhuma Corridor Feature Line " +
                    "com Code '" +
                    code +
                    "' foi encontrada.");
            }


            ed.WriteMessage(
                "\n\nTotal '" +
                code +
                "' = " +
                total.ToString(
                    "0.000000",
                    CultureInfo.InvariantCulture) +
                " m");


            return total;
        }


        // =====================================================================
        // REPARO LINEAR POR CONTINUIDADE LT/RT - v0.16
        //
        // Corrige apenas falhas pontuais do Detailed Report fracionado, quando
        // um item linear de Corridor Feature Line perde um dos lados no meio de
        // uma sequência em que os intervalos vizinhos possuem LT e RT.
        //
        // A quantidade total do Alignment nativo é a referência de fechamento.
        // O método NÃO tenta recalcular toda a geometria do Corridor.
        // =====================================================================

        private static double AplicarReparoLinearPorContinuidade(
            List<QtoIntervalo> intervalos,
            QtoReportData totalAlignment,
            Editor ed,
            out int correcoes)
        {
            correcoes = 0;
            double acrescimoTotal = 0.0;

            if (intervalos.Count < 3)
                return 0.0;

            Dictionary<string, double> somaNativa =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (QtoIntervalo intervalo in intervalos)
            {
                foreach (KeyValuePair<string, QtoPayItem> par in intervalo.Relatorio.PayItems)
                {
                    if (!somaNativa.ContainsKey(par.Key))
                        somaNativa[par.Key] = 0.0;

                    somaNativa[par.Key] += par.Value.Quantidade;
                }
            }

            foreach (KeyValuePair<string, QtoPayItem> totalPar in totalAlignment.PayItems)
            {
                string payItemId = totalPar.Key;
                QtoPayItem referencia = totalPar.Value;

                if (!string.Equals(
                    NormalizarUnidade(referencia.Unidade),
                    "m",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (referencia.CodigoFonte.IndexOf(
                    "Corridor Feature Line:",
                    StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                double calculado =
                    somaNativa.TryGetValue(payItemId, out double soma)
                        ? soma
                        : 0.0;

                double deficit =
                    referencia.Quantidade - calculado;

                if (deficit <= 0.25)
                    continue;

                List<(QtoIntervalo Intervalo, QtoPayItem Item, double Candidato)> candidatos =
                    new List<(QtoIntervalo, QtoPayItem, double)>();

                for (int i = 1; i < intervalos.Count - 1; i++)
                {
                    if (!intervalos[i - 1].Relatorio.PayItems.TryGetValue(
                        payItemId,
                        out QtoPayItem? anterior))
                    {
                        continue;
                    }

                    if (!intervalos[i].Relatorio.PayItems.TryGetValue(
                        payItemId,
                        out QtoPayItem? atual))
                    {
                        continue;
                    }

                    if (!intervalos[i + 1].Relatorio.PayItems.TryGetValue(
                        payItemId,
                        out QtoPayItem? posterior))
                    {
                        continue;
                    }

                    bool anteriorDoisLados =
                        TemLado(anterior, "LT") && TemLado(anterior, "RT");

                    bool posteriorDoisLados =
                        TemLado(posterior, "LT") && TemLado(posterior, "RT");

                    bool atualLT = TemLado(atual, "LT");
                    bool atualRT = TemLado(atual, "RT");

                    if (!anteriorDoisLados || !posteriorDoisLados)
                        continue;

                    // Precisa existir exatamente um lado no intervalo atual.
                    if (atualLT == atualRT)
                        continue;

                    string ladoExistente = atualLT ? "LT" : "RT";
                    double quantidadeLadoExistente =
                        atual.QuantidadePorLado[ladoExistente];

                    if (quantidadeLadoExistente <= 0.25)
                        continue;

                    candidatos.Add((
                        intervalos[i],
                        atual,
                        quantidadeLadoExistente));
                }

                if (candidatos.Count == 0)
                    continue;

                double somaCandidatos =
                    candidatos.Sum(x => x.Candidato);

                if (somaCandidatos <= 0.0)
                    continue;

                // Só corrige quando os lados ausentes detectados explicam o
                // déficit global com boa aproximação. Isso evita transformar
                // uma mudança legítima de seção em "erro".
                double incompatibilidade =
                    Math.Abs(somaCandidatos - deficit) /
                    Math.Max(deficit, 0.000001);

                if (incompatibilidade > 0.25)
                {
                    ed.WriteMessage(
                        "\n    [REPARO LINEAR NÃO APLICADO] " +
                        payItemId +
                        " | déficit=" +
                        deficit.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR")) +
                        " m | candidatos=" +
                        somaCandidatos.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR")) +
                        " m | incompatibilidade=" +
                        (incompatibilidade * 100.0).ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) +
                        "%");

                    continue;
                }

                // Distribui exatamente o déficit global entre os intervalos
                // candidatos, proporcionalmente ao lado que permaneceu no XML.
                foreach (var candidato in candidatos)
                {
                    double acrescimo =
                        deficit *
                        candidato.Candidato /
                        somaCandidatos;

                    candidato.Item.Quantidade += acrescimo;

                    if (!candidato.Item.CodigoFonte.Contains(
                        "REPARO_CONTINUIDADE_LT_RT",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        candidato.Item.CodigoFonte +=
                            " | REPARO_CONTINUIDADE_LT_RT";
                    }

                    correcoes++;
                    acrescimoTotal += acrescimo;

                    ed.WriteMessage(
                        "\n    [REPARO LINEAR LT/RT] " +
                        payItemId +
                        " | " +
                        FormatarEstaca(candidato.Intervalo.Inicio) +
                        " -> " +
                        FormatarEstaca(candidato.Intervalo.Fim) +
                        " | +" +
                        acrescimo.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR")) +
                        " m | fechamento no total nativo do Alignment");
                }
            }

            return acrescimoTotal;
        }


        private static bool TemLado(
            QtoPayItem item,
            string lado)
        {
            return
                item.QuantidadePorLado.TryGetValue(lado, out double q) &&
                q > 0.000001;
        }


        // =====================================================================
        // LEGADO v0.15 - CORREÇÃO GEOMÉTRICA (MANTIDO PARA DIAGNÓSTICO; NÃO É CHAMADO NA v0.16)
        //
        // O GenerateXMLReport fracionado pode, em situações pontuais, perder
        // uma das Feature Lines de um mesmo Point Code em um intervalo, mesmo
        // que o relatório total do Alignment esteja correto.
        //
        // Estratégia conservadora:
        // - só avalia Pay Items em "m" cuja fonte seja "Corridor Feature Line";
        // - usa apenas Baselines do Corridor que trabalham com o Alignment de
        //   referência, portanto a Station é diretamente comparável;
        // - calcula o comprimento 2D real das Feature Lines no intervalo;
        // - só SUBSTITUI quando a geometria for materialmente MAIOR que o valor
        //   nativo (mínimo 0,25 m e 1%); nunca reduz o QTO nativo.
        // =====================================================================

        private static double AplicarCorrecaoLinearFeatureLines(
            CivilCorridor corridor,
            ObjectId alignmentIdReferencia,
            double inicio,
            double fim,
            QtoReportData relatorio,
            Editor ed,
            out int correcoes)
        {
            correcoes = 0;

            double acrescimoTotal =
                0.0;

            foreach (
                QtoPayItem item
                in relatorio.PayItems.Values.ToList())
            {
                if (!string.Equals(
                    NormalizarUnidade(item.Unidade),
                    "m",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                List<string> codigos =
                    ExtrairCodigosCorridorFeatureLine(
                        item.CodigoFonte);

                if (codigos.Count == 0)
                    continue;

                double geometrico =
                    0.0;

                int featureLinesEncontradas =
                    0;

                foreach (string code in codigos)
                {
                    int flDoCode;

                    geometrico +=
                        CalcularComprimentoCorridorFeatureLine2D(
                            corridor,
                            alignmentIdReferencia,
                            code,
                            inicio,
                            fim,
                            out flDoCode);

                    featureLinesEncontradas +=
                        flDoCode;
                }

                if (
                    featureLinesEncontradas == 0 ||
                    geometrico <= 0.0)
                {
                    continue;
                }

                double nativo =
                    item.Quantidade;

                double toleranciaCorrecao =
                    Math.Max(
                        0.25,
                        geometrico * 0.01);

                if (
                    geometrico <=
                    nativo +
                    toleranciaCorrecao)
                {
                    continue;
                }

                double acrescimo =
                    geometrico -
                    nativo;

                item.Quantidade =
                    geometrico;

                if (!item.CodigoFonte.Contains(
                    "CORRECAO_GEOMETRICA_FL_2D",
                    StringComparison.OrdinalIgnoreCase))
                {
                    item.CodigoFonte =
                        item.CodigoFonte +
                        " | CORRECAO_GEOMETRICA_FL_2D";
                }

                correcoes++;
                acrescimoTotal +=
                    acrescimo;

                ed.WriteMessage(
                    "\n    [CORREÇÃO LINEAR] " +
                    item.Id +
                    " | " +
                    FormatarEstaca(inicio) +
                    " -> " +
                    FormatarEstaca(fim) +
                    " | nativo=" +
                    nativo.ToString(
                        "0.###",
                        CultureInfo.GetCultureInfo("pt-BR")) +
                    " m | geométrico=" +
                    geometrico.ToString(
                        "0.###",
                        CultureInfo.GetCultureInfo("pt-BR")) +
                    " m | +" +
                    acrescimo.ToString(
                        "0.###",
                        CultureInfo.GetCultureInfo("pt-BR")) +
                    " m");
            }

            return acrescimoTotal;
        }


        private static List<string> ExtrairCodigosCorridorFeatureLine(
            string codigoFonte)
        {
            List<string> codigos =
                new List<string>();

            if (string.IsNullOrWhiteSpace(codigoFonte))
                return codigos;

            const string marcador =
                "Corridor Feature Line:";

            foreach (
                string parte
                in codigoFonte.Split(
                    new[] { ',' },
                    StringSplitOptions.RemoveEmptyEntries))
            {
                int idx =
                    parte.IndexOf(
                        marcador,
                        StringComparison.OrdinalIgnoreCase);

                if (idx < 0)
                    continue;

                string code =
                    parte
                        .Substring(
                            idx +
                            marcador.Length)
                        .Trim();

                int idxPipe =
                    code.IndexOf('|');

                if (idxPipe >= 0)
                {
                    code =
                        code
                            .Substring(
                                0,
                                idxPipe)
                            .Trim();
                }

                if (
                    code.Length > 0 &&
                    !codigos.Contains(
                        code,
                        StringComparer.OrdinalIgnoreCase))
                {
                    codigos.Add(
                        code);
                }
            }

            return codigos;
        }


        private static double CalcularComprimentoCorridorFeatureLine2D(
            CivilCorridor corridor,
            ObjectId alignmentIdReferencia,
            string code,
            double inicio,
            double fim,
            out int featureLinesEncontradas)
        {
            double total =
                0.0;

            featureLinesEncontradas =
                0;

            foreach (
                Baseline baseline
                in corridor.Baselines)
            {
                ObjectId alignmentId;

                try
                {
                    alignmentId =
                        baseline.AlignmentId;
                }
                catch
                {
                    continue;
                }

                if (
                    alignmentId.IsNull ||
                    alignmentId != alignmentIdReferencia)
                {
                    continue;
                }

                try
                {
                    int flBaseline;

                    double parcial =
                        CalcularComprimentoFeatureLine2DSilencioso(
                            baseline,
                            code,
                            inicio,
                            fim,
                            out flBaseline);

                    if (flBaseline > 0)
                    {
                        total +=
                            parcial;

                        featureLinesEncontradas +=
                            flBaseline;
                    }
                }
                catch
                {
                    // Baselines especiais podem não expor MainBaselineFeatureLines.
                    // Nesses casos não arriscamos corrigir o valor nativo.
                }
            }

            return total;
        }


        private static double CalcularComprimentoFeatureLine2DSilencioso(
            Baseline baseline,
            string code,
            double inicio,
            double fim,
            out int featureLinesEncontradas)
        {
            double total =
                0.0;

            featureLinesEncontradas =
                0;

            BaselineFeatureLines baselineFeatureLines =
                baseline.MainBaselineFeatureLines;

            FeatureLineCollectionMap mapa =
                baselineFeatureLines.FeatureLineCollectionMap;

            foreach (
                FeatureLineCollection colecao
                in mapa)
            {
                foreach (
                    CorridorFeatureLine featureLine
                    in colecao)
                {
                    if (!string.Equals(
                        featureLine.CodeName,
                        code,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    featureLinesEncontradas++;

                    List<FeatureLinePoint> pontos =
                        new List<FeatureLinePoint>();

                    foreach (
                        FeatureLinePoint ponto
                        in featureLine.FeatureLinePoints)
                    {
                        pontos.Add(
                            ponto);
                    }

                    pontos =
                        pontos
                            .OrderBy(
                                p => p.Station)
                            .ToList();

                    if (pontos.Count < 2)
                        continue;

                    for (
                        int i = 0;
                        i < pontos.Count - 1;
                        i++)
                    {
                        FeatureLinePoint p0 =
                            pontos[i];

                        FeatureLinePoint p1 =
                            pontos[i + 1];

                        double s0 =
                            p0.Station;

                        double s1 =
                            p1.Station;

                        if (
                            s1 <=
                            s0 +
                            ToleranciaEstacao)
                        {
                            continue;
                        }

                        if (
                            s1 <=
                            inicio +
                            ToleranciaEstacao)
                        {
                            continue;
                        }

                        if (
                            s0 >=
                            fim -
                            ToleranciaEstacao)
                        {
                            continue;
                        }

                        double recorteInicio =
                            Math.Max(
                                s0,
                                inicio);

                        double recorteFim =
                            Math.Min(
                                s1,
                                fim);

                        if (
                            recorteFim <=
                            recorteInicio +
                            ToleranciaEstacao)
                        {
                            continue;
                        }

                        XY xyInicio =
                            InterpolarFeatureLineXY(
                                p0,
                                p1,
                                recorteInicio);

                        XY xyFim =
                            InterpolarFeatureLineXY(
                                p0,
                                p1,
                                recorteFim);

                        total +=
                            Distancia(
                                xyInicio,
                                xyFim);
                    }
                }
            }

            return total;
        }


        // =====================================================================
        // INTERPOLAR FEATURE LINE EM XY
        //
        // Utilizada quando início/fim do trecho solicitado cai no meio de um
        // segmento da Corridor Feature Line.
        //
        // Nesta versão é interpolação linear entre os FeatureLinePoints.
        // =====================================================================

        private static XY InterpolarFeatureLineXY(
            FeatureLinePoint a,
            FeatureLinePoint b,
            double station)
        {
            double deltaStation =
                b.Station -
                a.Station;


            if (
                Math.Abs(
                    deltaStation) <
                1e-12)
            {
                return new XY
                {
                    X =
                        a.XYZ.X,

                    Y =
                        a.XYZ.Y
                };
            }


            double t =
                (station - a.Station) /
                deltaStation;


            t =
                Math.Max(
                    0.0,
                    Math.Min(
                        1.0,
                        t));


            return new XY
            {
                X =
                        a.XYZ.X +
                        (
                            b.XYZ.X -
                            a.XYZ.X
                        ) *
                        t,

                Y =
                        a.XYZ.Y +
                        (
                            b.XYZ.Y -
                            a.XYZ.Y
                        ) *
                        t
            };
        }


        // =====================================================================
        // OBTER ESTAÇÃO DO APPLIED ASSEMBLY
        //
        // A API não precisa de uma propriedade Station própria aqui:
        // qualquer CalculatedPoint do AppliedAssembly contém:
        //
        // X = Station
        // Y = Offset
        // Z = Elevation relative to baseline
        // =====================================================================

        private static double? ObterEstacaoAssembly(
            AppliedAssembly assembly)
        {
            foreach (
                CalculatedPoint ponto
                in assembly.Points)
            {
                return
                    ponto
                        .StationOffsetElevationToBaseline
                        .X;
            }


            return null;
        }


        // =====================================================================
        // STATION / OFFSET -> XY REAL
        // =====================================================================

        private static XY ObterXY(
            Alignment alignment,
            double station,
            double offset)
        {
            double easting =
                0.0;


            double northing =
                0.0;


            alignment.PointLocation(
                station,
                offset,
                ref easting,
                ref northing);


            return new XY
            {
                X =
                        easting,

                Y =
                        northing
            };
        }


        // =====================================================================
        // ÁREA DE POLÍGONO EM PLANTA - SHOELACE
        // =====================================================================

        private static double AreaPoligono(
            params XY[] pontos)
        {
            if (pontos.Length < 3)
                return 0.0;


            // ================================================================
            // Usa coordenadas locais para evitar perda de precisão numérica
            // devido às coordenadas UTM grandes.
            // ================================================================

            double origemX =
                pontos[0].X;


            double origemY =
                pontos[0].Y;


            double soma =
                0.0;


            for (
                int i = 0;
                i < pontos.Length;
                i++)
            {
                int j =
                    (i + 1) %
                    pontos.Length;


                double xi =
                    pontos[i].X -
                    origemX;


                double yi =
                    pontos[i].Y -
                    origemY;


                double xj =
                    pontos[j].X -
                    origemX;


                double yj =
                    pontos[j].Y -
                    origemY;


                soma +=
                    xi * yj -
                    xj * yi;
            }


            return
                Math.Abs(
                    soma) /
                2.0;
        }


        // =====================================================================
        // DISTÂNCIA 2D
        // =====================================================================

        private static double Distancia(
            XY a,
            XY b)
        {
            double dx =
                b.X -
                a.X;


            double dy =
                b.Y -
                a.Y;


            return
                Math.Sqrt(
                    dx * dx +
                    dy * dy);
        }


        // =====================================================================
        // FORMATAR ESTAÇÃO
        // =====================================================================

        private static string FormatarEstaca(
            double valor)
        {
            const double comprimentoEstaca = 20.0;

            double estacaBase =
                Math.Floor(
                    valor /
                    comprimentoEstaca);

            int estaca =
                (int)estacaBase;

            double resto =
                valor -
                estacaBase *
                comprimentoEstaca;

            resto =
                Math.Round(
                    resto,
                    2,
                    MidpointRounding.AwayFromZero);

            if (resto >= comprimentoEstaca - 0.000001)
            {
                estaca++;
                resto = 0.0;
            }

            if (Math.Abs(resto) < 0.000001)
                resto = 0.0;

            return
                estaca.ToString(CultureInfo.InvariantCulture) +
                "+" +
                resto.ToString("0.00", CultureInfo.InvariantCulture);
        }


        // =====================================================================
        // HELP
        // =====================================================================

        [AcRuntime.CommandMethod(C3DCommands.Qto.Help)]
        public void Help()
        {
            Document? doc =
                AcApp.DocumentManager.MdiActiveDocument;

            if (doc == null)
                return;

            Editor ed =
                doc.Editor;

            ed.WriteMessage(
                "\n\n====================================================" +
                $"\n QTO v{Versao} - HELP" +
                "\n====================================================" +
                "\n" +
                "\n" + C3DCommands.Qto.Estudo +
                "\n  Comando principal da v0.16." +
                "\n  Seleciona Corridor + Alignment de referência." +
                "\n  Pergunta o intervalo em metros (20 m padrão)." +
                "\n  Gera Takeoff NATIVO detalhado para cada intervalo." +
                "\n  Filtra cada item do XML prioritariamente pelo AutoCADObjectHandle do Corridor selecionado." +
                "\n  Itens sem Handle podem ser aceitos pelo baselineName, somente quando a Baseline/Alignment pertence ao Corridor." +
                "\n  Isso permite usar o comando em DWG com vários Corridors sem misturar quantidades." +
                "\n  Soma os intervalos filtrados por Pay Item." +
                "\n  Compara com o Takeoff filtrado do Alignment inteiro." +
                "\n  Tenta também comparar com o Takeoff global do desenho, filtrado pelo mesmo Corridor." +
                "\n  Até 5% = OK; acima de 5% = DIVERGÊNCIA." +
                "\n  Gera XLSX formatado, CSVs, resumo TXT e todos os XMLs." +
                "\n  A saída fica em uma pasta estável QTO_<DWG>__<CORREDOR>." +
                "\n  Gera também <DWG>__<CORREDOR>__RESUMO_ORCAMENTO.csv," +
                "\n  sem identificadores internos do Civil 3D, para consolidação externa." +
                "\n" +
                "\nEXCEL" +
                "\n  QTO por Estaca: Pay Item, descrição, Code/Fonte," +
                "\n  Área m², Volume m³ e Comprimento m." +
                "\n  Resumo QTO: padrão vintage marrom/bege com Corredor + Alignment," +
                "\n  extensão, layer, baselines e auditoria do Takeoff." +
                "\n  Elementos geométricos ficam na própria aba Resumo QTO." +
                "\n  Metadados: mesma identidade visual, versão, critérios e rastreabilidade." +
                "\n  Soma por Estacas, diferenças, percentuais, status, Área/Volume/Comprimento" +
                "\n  e Chave são fórmulas reais do Excel." +
                "\n  Se a memória XLSX estiver aberta, salva uma cópia __ATUALIZADA_<data_hora>." +
                "\n  Estacas no padrão brasileiro de 20 m: 1+0.00." +
                "\n" +
                "\nMULTIPLOS CORRIDORS / LARGURA" +
                "\n  A opção de offset lateral permanece removida porque não há filtro" +
                "\n  lateral efetivo no GenerateXMLReport da API QTO 2026." +
                "\n  A separação entre Corridors é feita primeiro pelo AutoCADObjectHandle do objeto." +
                "\n  Quando o item não traz Handle, usa-se baselineName como fallback controlado." +
                "\n  Itens lineares de Corridor Feature Line usam reparo conservador de continuidade LT/RT" +
                "\n  quando um lado desaparece isoladamente entre intervalos com os dois lados." +
                "\n  Nesta v0.16, cada execução gera somente o Corridor selecionado." +
                "\n" +
                "\nDIMENSÃO" +
                "\n  A v0.16 não força 2D/3D." +
                "\n  Total e intervalos usam o mesmo QTOUtility e, portanto," +
                "\n  são comparados na mesma dimensão nativa." +
                "\n" +
                "\n" + C3DCommands.Qto.GeoTeste +
                "\n  Mantém o diagnóstico geométrico 2D da v0.6." +
                "\n" +
                "\n" + C3DCommands.Qto.Teste +
                "\n  Confirma carregamento da classe QTO." +
                "\n" +
                "\nA rotina não altera Corridor nem DWG." +
                "\n====================================================\n");
        }


        // =====================================================================
        // MODELOS INTERNOS
        // =====================================================================

        private sealed class QtoPayItem
        {
            public string Id { get; set; } = string.Empty;

            public string Descricao { get; set; } = string.Empty;

            public string Unidade { get; set; } = string.Empty;

            public string CodigoFonte { get; set; } = string.Empty;

            public double Quantidade { get; set; }

            public Dictionary<string, double> QuantidadePorLado { get; set; } =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }


        private sealed class QtoReportData
        {
            public string Xml { get; set; } = string.Empty;

            public Dictionary<string, QtoPayItem> PayItems { get; set; } =
                new Dictionary<string, QtoPayItem>(StringComparer.OrdinalIgnoreCase);

            public string[] GeneratedPayItemIds { get; set; } =
                Array.Empty<string>();

            public string CorridorHandleFiltro { get; set; } = string.Empty;

            public int ItensDetalhados { get; set; }

            public int ItensCorrespondentes { get; set; }

            public int ItensDescartados { get; set; }

            public int ItensIncluidosPorBaselineSemHandle { get; set; }

            public int ItensSemHandleDescartados { get; set; }

            public List<string> HandlesEncontrados { get; set; } =
                new List<string>();
        }


        private sealed class QtoIntervalo
        {
            public int Indice { get; set; }

            public double Inicio { get; set; }

            public double Fim { get; set; }

            public string Xml { get; set; } = string.Empty;

            public QtoReportData Relatorio { get; set; } =
                new QtoReportData();
        }


        private sealed class AuditoriaResultado
        {
            public int TotalItens { get; set; }

            public int Ok { get; set; }

            public int Divergentes { get; set; }

            public int SemReferencia { get; set; }
        }


        private sealed class Link2D
        {
            public double Offset1 { get; set; }


            public double Offset2 { get; set; }


            public double OffsetMedio { get; set; }
        }


        private struct XY
        {
            public double X;


            public double Y;
        }
    }


    // =====================================================================
    // EXPORTAÇÃO EXCEL - XLSX VINTAGE / PROCX - v0.16
    //
    // Mantida no mesmo arquivo para facilitar a substituição do módulo no
    // projeto mestre. Isso não altera o desempenho da DLL após compilação.
    // =====================================================================

    internal sealed class QtoExcelContext
    {
        public string Versao { get; set; } = string.Empty;
        public string Dwg { get; set; } = string.Empty;
        public string Corredor { get; set; } = string.Empty;
        public string Alignment { get; set; } = string.Empty;
        public string AlignmentTipo { get; set; } = string.Empty;
        public string AlignmentLayer { get; set; } = string.Empty;
        public string AlignmentDescricao { get; set; } = string.Empty;
        public string EstacaInicial { get; set; } = string.Empty;
        public string EstacaFinal { get; set; } = string.Empty;
        public double ExtensaoGeometrica { get; set; }
        public double AmplitudeEstaqueamento { get; set; }
        public double IntervaloMetros { get; set; }
        public double ToleranciaPercentual { get; set; }
        public DateTime GeradoEm { get; set; }
        public List<string> Baselines { get; set; } = new List<string>();
        public List<QtoExcelDetailRow> Detalhes { get; set; } = new List<QtoExcelDetailRow>();
        public List<QtoExcelAuditRow> Auditoria { get; set; } = new List<QtoExcelAuditRow>();
        public List<QtoAlignmentElementInfo> Elementos { get; set; } = new List<QtoAlignmentElementInfo>();
        public string CorridorHandle { get; set; } = string.Empty;
        public int ItensRelatorioTotal { get; set; }
        public int ItensDoCorridor { get; set; }
        public int ItensDescartados { get; set; }
        public List<string> HandlesEncontrados { get; set; } = new List<string>();
        public int CorrecoesLineares { get; set; }
        public double AcrescimoCorrecoesLineares { get; set; }
    }

    internal sealed class QtoExcelDetailRow
    {
        public string Trecho { get; set; } = string.Empty;
        public string EstacaInicial { get; set; } = string.Empty;
        public string EstacaFinal { get; set; } = string.Empty;
        public string Chave { get; set; } = string.Empty;
        public string PayItem { get; set; } = string.Empty;
        public string Descricao { get; set; } = string.Empty;
        public string CodigoFonte { get; set; } = string.Empty;
        public string Unidade { get; set; } = string.Empty;
        public double? Area { get; set; }
        public double? Volume { get; set; }
        public double? Comprimento { get; set; }
        public double QuantidadeNativa { get; set; }
    }

    internal sealed class QtoExcelAuditRow
    {
        public string PayItem { get; set; } = string.Empty;
        public string Descricao { get; set; } = string.Empty;
        public string Unidade { get; set; } = string.Empty;
        public double SomaIntervalos { get; set; }
        public double? TakeoffAlignment { get; set; }
        public double? DiferencaAbsAlignment { get; set; }
        public double? DiferencaPctAlignment { get; set; }
        public string StatusAlignment { get; set; } = string.Empty;
        public double? TakeoffDesenho { get; set; }
        public double? DiferencaAbsDesenho { get; set; }
        public double? DiferencaPctDesenho { get; set; }
        public string StatusDesenho { get; set; } = string.Empty;
    }

    internal sealed class QtoAlignmentElementInfo
    {
        public int Ordem { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public string TipoNativo { get; set; } = string.Empty;
        public double? EstacaInicial { get; set; }
        public double? EstacaFinal { get; set; }
        public string EstacaInicialFormatada { get; set; } = string.Empty;
        public string EstacaFinalFormatada { get; set; } = string.Empty;
        public double? Extensao { get; set; }
        public double? Raio { get; set; }
        public string Detalhes { get; set; } = string.Empty;
    }

    internal static class QtoExcelExporter
    {
        // -----------------------------------------------------------------
        // ESTILOS
        // Paleta inspirada na planilha de referência "vintage":
        // marrom técnico + bege + creme, inspirado na memória de cálculo de referência.
        // -----------------------------------------------------------------
        private const int StyleNormal = 0;
        private const int StyleTitle = 1;
        private const int StyleSubtitle = 2;
        private const int StyleHeader = 3;
        private const int StyleText = 4;
        private const int StyleNumber2 = 5;
        private const int StyleNumber3 = 6;
        private const int StyleStatusOk = 7;
        private const int StyleStatusBad = 8;
        private const int StyleLabel = 9;
        private const int StyleValue = 10;
        private const int StyleSection = 11;
        private const int StylePercent = 12;
        private const int StyleNumberBold = 13;
        private const int StyleNote = 14;
        private const int StyleBandText = 15;
        private const int StyleBandNumber3 = 16;
        private const int StyleBandPercent = 17;

        public static void Exportar(
            string caminho,
            QtoExcelContext ctx)
        {
            string? dir = Path.GetDirectoryName(caminho);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(caminho))
                File.Delete(caminho);

            // IMPORTANTE:
            // o pacote é fechado antes da validação. Na v0.8 as três
            // primeiras planilhas eram reparadas pelo Excel porque o XML
            // escrevia mergeCells antes de autoFilter, ordem inválida no
            // schema SpreadsheetML. Na v0.14 a ordem permanece corrigida e é
            // conferida novamente em ValidarPacoteXlsx().
            using (FileStream fs = new FileStream(
                caminho,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None))
            using (ZipArchive zip = new ZipArchive(
                fs,
                ZipArchiveMode.Create,
                leaveOpen: false,
                entryNameEncoding: Encoding.UTF8))
            {
                WriteEntry(zip, "[Content_Types].xml", ContentTypesXml());
                WriteEntry(zip, "_rels/.rels", RootRelsXml());
                WriteEntry(zip, "xl/workbook.xml", WorkbookXml());
                WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml());
                WriteEntry(zip, "xl/styles.xml", StylesXml());
                WriteEntry(zip, "xl/worksheets/sheet1.xml", SheetDetalhado(ctx));
                WriteEntry(zip, "xl/worksheets/sheet2.xml", SheetResumo(ctx));
                WriteEntry(zip, "xl/worksheets/sheet3.xml", SheetMetadados(ctx));
            }

            ValidarPacoteXlsx(caminho);
        }

        private static void WriteEntry(
            ZipArchive zip,
            string path,
            string content)
        {
            ZipArchiveEntry entry = zip.CreateEntry(
                path,
                CompressionLevel.Optimal);

            using Stream stream = entry.Open();
            using StreamWriter writer = new StreamWriter(
                stream,
                new UTF8Encoding(false));

            writer.Write(content);
        }

        private static string ContentTypesXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                   "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                   "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                   "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                   "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                   "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                   "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                   "<Override PartName=\"/xl/worksheets/sheet3.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                   "</Types>";
        }

        private static string RootRelsXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                   "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                   "</Relationships>";
        }

        private static string WorkbookXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                   "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                   "<bookViews><workbookView xWindow=\"0\" yWindow=\"0\" windowWidth=\"22000\" windowHeight=\"12000\"/></bookViews>" +
                   "<sheets>" +
                   "<sheet name=\"QTO por Estaca\" sheetId=\"1\" r:id=\"rId1\"/>" +
                   "<sheet name=\"Resumo QTO\" sheetId=\"2\" r:id=\"rId2\"/>" +
                   "<sheet name=\"Metadados\" sheetId=\"3\" r:id=\"rId3\"/>" +
                   "</sheets>" +
                   "<calcPr calcId=\"191029\" fullCalcOnLoad=\"1\" forceFullCalc=\"1\"/>" +
                   "</workbook>";
        }

        private static string WorkbookRelsXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                   "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                   "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
                   "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet3.xml\"/>" +
                   "<Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                   "</Relationships>";
        }

        private static string StylesXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                   "<numFmts count=\"2\">" +
                   "<numFmt numFmtId=\"164\" formatCode=\"#,##0.000\"/>" +
                   "<numFmt numFmtId=\"165\" formatCode=\"0.000\\%\"/>" +
                   "</numFmts>" +
                   "<fonts count=\"8\">" +
                   FontXml("Consolas", 10, false, "FF40382F") +
                   FontXml("Consolas", 15, true, "FFFFFFFF") +
                   FontXml("Consolas", 10, true, "FF6B5B4D") +
                   FontXml("Consolas", 10, true, "FF2F5D3A") +
                   FontXml("Consolas", 10, true, "FF8B2C2C") +
                   FontXml("Consolas", 10, true, "FF6B5B4D") +
                   FontXml("Consolas", 11, false, "FF6B5B4D", true) +
                   FontXml("Consolas", 13, true, "FF6B5B4D") +
                   "</fonts>" +
                   "<fills count=\"9\">" +
                   "<fill><patternFill patternType=\"none\"/></fill>" +
                   "<fill><patternFill patternType=\"gray125\"/></fill>" +
                   FillXml("FF6B5B4D") +        // marrom principal
                   FillXml("FFD8CBB8") +        // bege de seção
                   FillXml("FFF4F0E6") +        // creme principal
                   FillXml("FFE3EFE6") +        // verde suave
                   FillXml("FFF4DEDE") +        // vermelho suave
                   FillXml("FFF1EBDD") +        // creme alternado / valores
                   FillXml("FFD9EAF7") +        // observação / nota técnica
                   "</fills>" +
                   "<borders count=\"3\">" +
                   "<border><left/><right/><top/><bottom/><diagonal/></border>" +
                   "<border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FFA99A88\"/></bottom><diagonal/></border>" +
                   "<border><left/><right/><top style=\"medium\"><color rgb=\"FF6B5B4D\"/></top><bottom style=\"medium\"><color rgb=\"FF6B5B4D\"/></bottom><diagonal/></border>" +
                   "</borders>" +
                   "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                   "<cellXfs count=\"18\">" +
                   Xf(0, 0, 0, 0, false, null) +          // 0 normal
                   Xf(0, 1, 2, 0, true, "center") +    // 1 título marrom
                   Xf(0, 6, 0, 0, true, "center") +    // 2 subtítulo itálico
                   Xf(0, 2, 4, 1, true, "center") +    // 3 cabeçalho creme
                   Xf(0, 0, 4, 1, true, "left") +      // 4 texto creme
                   Xf(4, 0, 7, 1, true, "right") +     // 5 número 2 casas
                   Xf(164, 0, 7, 1, true, "right") +     // 6 número 3 casas
                   Xf(0, 3, 5, 1, true, "center") +    // 7 OK
                   Xf(0, 4, 6, 1, true, "center") +    // 8 divergência
                   Xf(0, 5, 4, 1, true, "left") +      // 9 label vintage
                   Xf(0, 0, 7, 1, true, "left") +      // 10 valor vintage
                   Xf(0, 7, 3, 2, true, "center") +    // 11 seção vintage
                   Xf(165, 0, 7, 1, true, "right") +     // 12 percentual
                   Xf(164, 5, 7, 1, true, "right") +     // 13 número bold
                   Xf(0, 0, 8, 0, true, "left") +      // 14 nota técnica
                   Xf(0, 0, 7, 1, true, "left") +      // 15 texto faixa alternada
                   Xf(164, 0, 4, 1, true, "right") +     // 16 número faixa alternada
                   Xf(165, 0, 4, 1, true, "right") +     // 17 percentual faixa alternada
                   "</cellXfs>" +
                   "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                   "<dxfs count=\"2\">" +
                   "<dxf><font><b/><color rgb=\"FF2F5D3A\"/></font><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE3EFE6\"/><bgColor indexed=\"64\"/></patternFill></fill></dxf>" +
                   "<dxf><font><b/><color rgb=\"FF8B2C2C\"/></font><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF4DEDE\"/><bgColor indexed=\"64\"/></patternFill></fill></dxf>" +
                   "</dxfs>" +
                   "<tableStyles count=\"0\" defaultTableStyle=\"TableStyleMedium2\" defaultPivotStyle=\"PivotStyleLight16\"/>" +
                   "</styleSheet>";
        }

        private static string FontXml(
            string name,
            double size,
            bool bold,
            string argb,
            bool italic = false)
        {
            return "<font>" +
                   (bold ? "<b/>" : string.Empty) +
                   (italic ? "<i/>" : string.Empty) +
                   "<sz val=\"" + size.ToString("0.##", CultureInfo.InvariantCulture) + "\"/>" +
                   "<color rgb=\"" + argb + "\"/>" +
                   "<name val=\"" + Xml(name) + "\"/>" +
                   "<family val=\"3\"/>" +
                   "</font>";
        }

        private static string FillXml(string argb)
        {
            return "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + argb + "\"/><bgColor indexed=\"64\"/></patternFill></fill>";
        }

        private static string BorderSide(string side)
        {
            return "<" + side + " style=\"thin\"><color rgb=\"FFD7CEBD\"/></" + side + ">";
        }

        private static string Xf(
            int numFmtId,
            int fontId,
            int fillId,
            int borderId,
            bool alignment,
            string? horizontal)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<xf numFmtId=\"");
            sb.Append(numFmtId.ToString(CultureInfo.InvariantCulture));
            sb.Append("\" fontId=\"");
            sb.Append(fontId.ToString(CultureInfo.InvariantCulture));
            sb.Append("\" fillId=\"");
            sb.Append(fillId.ToString(CultureInfo.InvariantCulture));
            sb.Append("\" borderId=\"");
            sb.Append(borderId.ToString(CultureInfo.InvariantCulture));
            sb.Append("\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyNumberFormat=\"1\"");

            if (!alignment)
            {
                sb.Append("/>");
                return sb.ToString();
            }

            sb.Append(" applyAlignment=\"1\">");
            sb.Append("<alignment vertical=\"center\" wrapText=\"1\"");
            if (!string.IsNullOrWhiteSpace(horizontal))
                sb.Append(" horizontal=\"").Append(horizontal).Append("\"");
            sb.Append("/>");
            sb.Append("</xf>");
            return sb.ToString();
        }

        // -----------------------------------------------------------------
        // ABA 1 - BASE TABULAR PARA PROCX
        // Cabeçalho obrigatoriamente na linha 1 e nenhuma célula mesclada.
        // -----------------------------------------------------------------
        private static string SheetDetalhado(QtoExcelContext ctx)
        {
            StringBuilder sb = SheetStart(1);
            sb.Append(Columns(new double[] { 24, 16, 16, 16, 15, 48, 24, 11, 16, 15, 15, 15 }));
            sb.Append("<sheetData>");

            RowBuilder row = new RowBuilder(sb);
            row.Begin(1, 28)
                .Text(1, "Trecho", StyleHeader)
                .Text(2, "Estaca Inicial", StyleHeader)
                .Text(3, "Estaca Final", StyleHeader)
                .Text(4, "Pay Item", StyleHeader)
                .Text(5, "Descrição", StyleHeader)
                .Text(6, "Code / Fonte", StyleHeader)
                .Text(7, "Unidade", StyleHeader)
                .Text(8, "Quantidade", StyleHeader)
                .Text(9, "Área m²", StyleHeader)
                .Text(10, "Volume m³", StyleHeader)
                .Text(11, "Compr. m", StyleHeader)
                .Text(12, "Chave", StyleHeader)
                .End();

            int excelRow = 2;
            string ultimoTrecho = string.Empty;
            int grupo = -1;

            foreach (QtoExcelDetailRow item in ctx.Detalhes)
            {
                if (!string.Equals(ultimoTrecho, item.Trecho, StringComparison.Ordinal))
                {
                    ultimoTrecho = item.Trecho;
                    grupo++;
                }

                bool creme = (grupo % 2 == 0);
                int textStyle = creme ? StyleBandText : StyleText;
                int numberStyle = creme ? StyleBandNumber3 : StyleNumber3;

                row.Begin(excelRow, 20)
                    .Text(1, item.Trecho, textStyle)
                    .Text(2, item.EstacaInicial, textStyle)
                    .Text(3, item.EstacaFinal, textStyle)
                    .Text(4, item.PayItem, textStyle)
                    .Text(5, item.Descricao, textStyle)
                    .Text(6, item.CodigoFonte, textStyle)
                    .Text(7, item.Unidade, textStyle)
                    .Number(8, item.QuantidadeNativa, numberStyle)
                    .FormulaNumber(9, "IF($G" + excelRow + "=\"m2\",$H" + excelRow + ",\"\")", item.Area, numberStyle)
                    .FormulaNumber(10, "IF($G" + excelRow + "=\"m3\",$H" + excelRow + ",\"\")", item.Volume, numberStyle)
                    .FormulaNumber(11, "IF($G" + excelRow + "=\"m\",$H" + excelRow + ",\"\")", item.Comprimento, numberStyle)
                    .FormulaText(12, "$A" + excelRow + "&\"|\"&$D" + excelRow, item.Chave, textStyle)
                    .End();

                excelRow++;
            }

            sb.Append("</sheetData>");

            // SpreadsheetML exige autoFilter ANTES de mergeCells.
            // Esta aba não possui merges justamente para ser uma base limpa.
            if (excelRow > 2)
                sb.Append("<autoFilter ref=\"A1:L").Append(excelRow - 1).Append("\"/>");

            sb.Append(PageSetupLandscape());
            sb.Append(SheetEnd());
            return sb.ToString();
        }

        // -----------------------------------------------------------------
        // ABA 2 - RESUMO / AUDITORIA
        // Layout vintage com cabeçalho técnico do Corredor + Alignment.
        // -----------------------------------------------------------------
        private static string SheetResumo(QtoExcelContext ctx)
        {
            StringBuilder sb = SheetStart(0);
            sb.Append(Columns(new double[] { 15, 30, 14, 18, 18, 16, 14, 16, 18, 16, 14, 18 }));
            sb.Append("<sheetData>");

            RowBuilder row = new RowBuilder(sb);

            // Cabeçalho técnico vintage: Corredor + Alignment.
            row.Begin(1, 30).Text(1, "MEMÓRIA DE CÁLCULO - QUANTITATIVOS DO CORREDOR", StyleTitle).End();
            row.Begin(2, 22).Text(1, "Corredor: " + ctx.Corredor, StyleSubtitle).End();
            row.Begin(3, 22).Text(1, "Alignment de referência: " + ctx.Alignment, StyleSubtitle).End();
            row.Begin(4, 23).Text(1, "IDENTIFICAÇÃO DO ESTUDO", StyleSection).End();

            row.Begin(5, 21)
                .Text(1, "Corredor", StyleLabel).Text(2, ctx.Corredor, StyleValue)
                .Text(5, "Alignment", StyleLabel).Text(6, ctx.Alignment, StyleValue)
                .Text(9, "Layer", StyleLabel).Text(10, ctx.AlignmentLayer, StyleValue)
                .End();

            row.Begin(6, 21)
                .Text(1, "Estaca inicial", StyleLabel).Text(2, ctx.EstacaInicial, StyleValue)
                .Text(3, "Estaca final", StyleLabel).Text(4, ctx.EstacaFinal, StyleValue)
                .Text(5, "Extensão (m)", StyleLabel).Number(6, ctx.ExtensaoGeometrica, StyleNumber3)
                .Text(7, "Intervalo", StyleLabel).Text(8, ctx.IntervaloMetros.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR")) + " m", StyleValue)
                .Text(9, "Tolerância", StyleLabel).Number(10, ctx.ToleranciaPercentual, StylePercent)
                .Text(11, "Elementos", StyleLabel).Number(12, ctx.Elementos.Count, StyleNumberBold)
                .End();

            int tangentes = ctx.Elementos.Count(x => x.Tipo == "Tangente");
            int curvas = ctx.Elementos.Count(x => x.Tipo == "Curva circular");
            int espirais = ctx.Elementos.Count(x => x.Tipo == "Espiral");

            row.Begin(7, 21)
                .Text(1, "Tipo Alignment", StyleLabel).Text(2, ctx.AlignmentTipo, StyleValue)
                .Text(5, "Tangentes", StyleLabel).Number(6, tangentes, StyleNumberBold)
                .Text(7, "Curvas", StyleLabel).Number(8, curvas, StyleNumberBold)
                .Text(9, "Espirais", StyleLabel).Number(10, espirais, StyleNumberBold)
                .Text(11, "Baselines", StyleLabel).Number(12, ctx.Baselines.Count, StyleNumberBold)
                .End();

            string baselinesTexto = ctx.Baselines.Count == 0
                ? "Nenhuma informação de baseline disponível."
                : string.Join(" | ", ctx.Baselines);

            row.Begin(8, 34)
                .Text(1, "Baselines do corredor", StyleLabel)
                .Text(2, baselinesTexto, StyleValue)
                .End();

            row.Begin(9, 7).End();
            row.Begin(10, 23).Text(1, "RESUMO / AUDITORIA", StyleSection).End();

            int headerAuditoria = 11;
            row.Begin(headerAuditoria, 30)
                .Text(1, "Pay Item", StyleHeader)
                .Text(2, "Descrição", StyleHeader)
                .Text(3, "Unidade", StyleHeader)
                .Text(4, "Soma por Estacas", StyleHeader)
                .Text(5, "Takeoff Alignment", StyleHeader)
                .Text(6, "Dif. Abs.", StyleHeader)
                .Text(7, "Dif. %", StyleHeader)
                .Text(8, "Status", StyleHeader)
                .Text(9, "Takeoff Desenho", StyleHeader)
                .Text(10, "Dif. Abs.", StyleHeader)
                .Text(11, "Dif. %", StyleHeader)
                .Text(12, "Status Desenho", StyleHeader)
                .End();

            int excelRow = headerAuditoria + 1;
            int indice = 0;
            foreach (QtoExcelAuditRow item in ctx.Auditoria)
            {
                bool creme = (indice % 2 == 0);
                int textStyle = creme ? StyleBandText : StyleText;
                int numberStyle = creme ? StyleBandNumber3 : StyleNumber3;
                int percentStyle = creme ? StyleBandPercent : StylePercent;
                int statusAliStyle = textStyle;
                int statusDesStyle = textStyle;

                string fSoma =
                    "SUMIFS('QTO por Estaca'!$H:$H,'QTO por Estaca'!$D:$D,$A" + excelRow + ")";

                string fDifAli =
                    "IF($E" + excelRow + "=\"\",\"\",ABS($D" + excelRow + "-$E" + excelRow + "))";

                string fPctAli =
                    "IF($E" + excelRow + "=\"\",\"\",IFERROR($F" + excelRow + "/ABS($E" + excelRow + ")*100,IF(ABS($D" + excelRow + ")<1E-9,0,100)))";

                string fStatusAli =
                    "IF($G" + excelRow + "=\"\",\"SEM REFERÊNCIA\",IF($G" + excelRow + "<=$J$6,\"OK\",\"DIVERGÊNCIA\"))";

                string fDifDes =
                    "IF($I" + excelRow + "=\"\",\"\",ABS($D" + excelRow + "-$I" + excelRow + "))";

                string fPctDes =
                    "IF($I" + excelRow + "=\"\",\"\",IFERROR($J" + excelRow + "/ABS($I" + excelRow + ")*100,IF(ABS($D" + excelRow + ")<1E-9,0,100)))";

                string fStatusDes =
                    "IF($K" + excelRow + "=\"\",\"SEM REFERÊNCIA\",IF($K" + excelRow + "<=$J$6,\"OK\",\"DIVERGÊNCIA\"))";

                row.Begin(excelRow, 21)
                    .Text(1, item.PayItem, textStyle)
                    .Text(2, item.Descricao, textStyle)
                    .Text(3, item.Unidade, textStyle)
                    .FormulaNumber(4, fSoma, item.SomaIntervalos, numberStyle)
                    .NullableNumber(5, item.TakeoffAlignment, numberStyle, textStyle)
                    .FormulaNumber(6, fDifAli, item.DiferencaAbsAlignment, numberStyle)
                    .FormulaNumber(7, fPctAli, item.DiferencaPctAlignment, percentStyle)
                    .FormulaText(8, fStatusAli, item.StatusAlignment, statusAliStyle)
                    .NullableNumber(9, item.TakeoffDesenho, numberStyle, textStyle)
                    .FormulaNumber(10, fDifDes, item.DiferencaAbsDesenho, numberStyle)
                    .FormulaNumber(11, fPctDes, item.DiferencaPctDesenho, percentStyle)
                    .FormulaText(12, fStatusDes, item.StatusDesenho, statusDesStyle)
                    .End();
                excelRow++;
                indice++;
            }

            int fimDados = excelRow - 1;
            int total = ctx.Auditoria.Count;
            int ok = ctx.Auditoria.Count(x => x.StatusAlignment == "OK");
            int div = ctx.Auditoria.Count(x => x.StatusAlignment == "DIVERGÊNCIA");

            excelRow += 1;
            row.Begin(excelRow, 23).Text(1, "INDICADORES", StyleSection).Text(2, "Resultado", StyleSection).End();
            row.Begin(excelRow + 1, 21).Text(1, "Pay Items auditados", StyleLabel).Number(2, total, StyleNumberBold).End();
            row.Begin(excelRow + 2, 21).Text(1, "OK (≤ tolerância)", StyleLabel).Number(2, ok, StyleNumberBold).End();
            row.Begin(excelRow + 3, 21).Text(1, "Divergências", StyleLabel).Number(2, div, StyleNumberBold).End();
            row.Begin(excelRow + 4, 40)
                .Text(1, "Observação", StyleLabel)
                .Text(2, "Takeoff Alignment e Takeoff Desenho são filtrados prioritariamente pelo AutoCADObjectHandle do Corridor selecionado. Itens sem Handle podem ser aceitos pelo baselineName somente quando ele pertence a uma Baseline/Alignment do próprio Corridor. O fechamento principal continua sendo Soma por Estacas × Takeoff Alignment.", StyleNote)
                .End();

            // Detalhamento geométrico permanece na própria aba Resumo QTO.
            int secGeom = excelRow + 6;
            row.Begin(secGeom, 23).Text(1, "ELEMENTOS GEOMÉTRICOS DO ALIGNMENT", StyleSection).End();
            int headerGeom = secGeom + 1;
            row.Begin(headerGeom, 28)
                .Text(1, "#", StyleHeader).Text(2, "Tipo", StyleHeader)
                .Text(3, "Estaca inicial", StyleHeader).Text(4, "Estaca final", StyleHeader)
                .Text(5, "Extensão (m)", StyleHeader).Text(6, "Raio (m)", StyleHeader)
                .Text(7, "Detalhes", StyleHeader).End();

            int rGeom = headerGeom + 1;
            int idxGeom = 0;
            foreach (QtoAlignmentElementInfo el in ctx.Elementos)
            {
                bool creme = (idxGeom % 2 == 0);
                int textStyle = creme ? StyleBandText : StyleText;
                int numberStyle = creme ? StyleBandNumber3 : StyleNumber3;
                row.Begin(rGeom, 21)
                    .Number(1, el.Ordem, numberStyle).Text(2, el.Tipo, textStyle)
                    .Text(3, el.EstacaInicialFormatada, textStyle).Text(4, el.EstacaFinalFormatada, textStyle)
                    .NullableNumber(5, el.Extensao, numberStyle, textStyle).NullableNumber(6, el.Raio, numberStyle, textStyle)
                    .Text(7, el.Detalhes, textStyle).End();
                rGeom++;
                idxGeom++;
            }

            sb.Append("</sheetData>");
            if (fimDados >= headerAuditoria + 1)
                sb.Append("<autoFilter ref=\"A").Append(headerAuditoria).Append(":L").Append(fimDados).Append("\"/>");

            // autoFilter precisa vir antes de mergeCells no SpreadsheetML.
            sb.Append("<mergeCells count=\"12\">")
              .Append("<mergeCell ref=\"A1:L1\"/>")
              .Append("<mergeCell ref=\"A2:L2\"/>")
              .Append("<mergeCell ref=\"A3:L3\"/>")
              .Append("<mergeCell ref=\"A4:L4\"/>")
              .Append("<mergeCell ref=\"B5:D5\"/>")
              .Append("<mergeCell ref=\"F5:H5\"/>")
              .Append("<mergeCell ref=\"J5:L5\"/>")
              .Append("<mergeCell ref=\"B7:D7\"/>")
              .Append("<mergeCell ref=\"B8:L8\"/>")
              .Append("<mergeCell ref=\"A10:L10\"/>")
              .Append("<mergeCell ref=\"B").Append(excelRow + 4).Append(":L").Append(excelRow + 4).Append("\"/>")
              .Append("<mergeCell ref=\"A").Append(secGeom).Append(":L").Append(secGeom).Append("\"/>")
              .Append("</mergeCells>");

            if (fimDados >= headerAuditoria + 1)
            {
                string faixaStatusAli =
                    "H" + (headerAuditoria + 1) + ":H" + fimDados;

                string faixaStatusDes =
                    "L" + (headerAuditoria + 1) + ":L" + fimDados;

                sb.Append("<conditionalFormatting sqref=\"").Append(faixaStatusAli).Append("\">")
                  .Append("<cfRule type=\"expression\" dxfId=\"0\" priority=\"1\"><formula>H")
                  .Append(headerAuditoria + 1).Append("=\"OK\"</formula></cfRule>")
                  .Append("<cfRule type=\"expression\" dxfId=\"1\" priority=\"2\"><formula>H")
                  .Append(headerAuditoria + 1).Append("=\"DIVERGÊNCIA\"</formula></cfRule>")
                  .Append("</conditionalFormatting>");

                sb.Append("<conditionalFormatting sqref=\"").Append(faixaStatusDes).Append("\">")
                  .Append("<cfRule type=\"expression\" dxfId=\"0\" priority=\"3\"><formula>L")
                  .Append(headerAuditoria + 1).Append("=\"OK\"</formula></cfRule>")
                  .Append("<cfRule type=\"expression\" dxfId=\"1\" priority=\"4\"><formula>L")
                  .Append(headerAuditoria + 1).Append("=\"DIVERGÊNCIA\"</formula></cfRule>")
                  .Append("</conditionalFormatting>");
            }

            sb.Append(PageSetupLandscape());
            sb.Append(SheetEnd());
            return sb.ToString();
        }

        // -----------------------------------------------------------------
        // ABA 3 - METADADOS
        // -----------------------------------------------------------------
        private static string SheetMetadados(QtoExcelContext ctx)
        {
            StringBuilder sb = SheetStart(0);
            sb.Append(Columns(new double[] { 34, 82 }));
            sb.Append("<sheetData>");

            RowBuilder row = new RowBuilder(sb);
            row.Begin(1, 28).Text(1, "METADADOS DA MEMÓRIA DE CÁLCULO", StyleTitle).End();
            row.Begin(2, 22).Text(1, "Rastreabilidade da extração, critérios de cálculo e parâmetros do relatório", StyleSubtitle).End();
            row.Begin(3, 7).End();
            row.Begin(4, 24).Text(1, "Campo", StyleHeader).Text(2, "Valor", StyleHeader).End();

            int r = 5;
            Meta(row, r++, "Versão QTO", ctx.Versao);
            Meta(row, r++, "DWG", ctx.Dwg);
            Meta(row, r++, "Corredor", ctx.Corredor);
            Meta(row, r++, "Alignment de referência", ctx.Alignment);
            Meta(row, r++, "Estaca inicial", ctx.EstacaInicial);
            Meta(row, r++, "Estaca final", ctx.EstacaFinal);
            Meta(row, r++, "Intervalo entre relatórios", ctx.IntervaloMetros.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR")) + " m");
            Meta(row, r++, "Tolerância de auditoria", ctx.ToleranciaPercentual.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) + "%");
            Meta(row, r++, "Motor de cálculo", "QTOUtility.GenerateXMLReport – Detailed Report nativo");
            Meta(row, r++, "Dimensão", "Dimensão nativa do QTO; total e intervalos gerados pelo mesmo motor");
            Meta(row, r++, "Filtro lateral", "Não aplicado – opção removida por não haver filtro lateral funcional na API QTO 2026");
            Meta(row, r++, "Filtro por Corridor", "Handle = " + ctx.CorridorHandle + "; fallback por baselineName somente para itens sem Handle e pertencentes ao próprio Corridor");
            Meta(row, r++, "Itens detalhados no TOTAL_ALIGNMENT", ctx.ItensRelatorioTotal.ToString(CultureInfo.InvariantCulture));
            Meta(row, r++, "Itens do Corridor selecionado", ctx.ItensDoCorridor.ToString(CultureInfo.InvariantCulture));
            Meta(row, r++, "Itens descartados", ctx.ItensDescartados.ToString(CultureInfo.InvariantCulture));
            Meta(row, r++, "Handles encontrados no TOTAL_ALIGNMENT", ctx.HandlesEncontrados.Count == 0 ? "(nenhum)" : string.Join(", ", ctx.HandlesEncontrados));
            Meta(row, r++, "Correções lineares automáticas", ctx.CorrecoesLineares.ToString(CultureInfo.InvariantCulture));
            Meta(row, r++, "Acréscimo por correção linear", ctx.AcrescimoCorrecoesLineares.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR")) + " m");
            Meta(row, r++, "Critério da correção linear", "Somente Corridor Feature Line; corrige apenas quando a geometria 2D do intervalo é materialmente maior que o QTO nativo; nunca reduz quantidade.");
            Meta(row, r++, "Critério de validação", "Soma dos intervalos filtrados × Takeoff filtrado do Alignment; diferença ≤ 5% = OK");
            Meta(row, r++, "Cálculos explícitos no Excel", "Soma por estacas, diferenças, percentuais, status, Área/Volume/Comprimento e Chave são fórmulas do Excel");
            Meta(row, r++, "Formato de estaca", "Padrão brasileiro de 20 m: 1+0.00");
            Meta(row, r++, "Estrutura da aba QTO por Estaca", "Tabela plana; cabeçalho na linha 1; coluna Chave = Trecho|Pay Item; adequada para PROCX/filtros");
            Meta(row, r++, "Gerado em", ctx.GeradoEm.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.GetCultureInfo("pt-BR")));

            r++;
            row.Begin(r, 22).Text(1, "OBSERVAÇÃO", StyleSection).Text(2, string.Empty, StyleSection).End();
            r++;
            row.Begin(r, 54).Text(1,
                "O fracionamento é executado pelo próprio motor nativo do Civil 3D por AlignmentId + StartStation + EndStation. " +
                "Na v0.16, cada item detalhado do XML é filtrado prioritariamente pelo AutoCADObjectHandle do Corridor selecionado; itens sem Handle podem entrar por baselineName quando pertencem ao próprio Corridor. " +
                "Assim, o mesmo DWG pode conter vários corredores; esta execução gera a memória somente do Corridor selecionado. " +
                "Para itens lineares de Corridor Feature Line, a v0.16 também confere cada intervalo pela geometria 2D das Feature Lines do próprio Corridor e corrige apenas perdas materiais do QTO fracionado. " +
                "A planilha mantém auditoria contra o total do Alignment e contra o DrawingExtent, ambos filtrados pelo mesmo Corridor.",
                StyleNote).End();

            sb.Append("</sheetData>");
            sb.Append("<mergeCells count=\"3\"><mergeCell ref=\"A1:B1\"/><mergeCell ref=\"A2:B2\"/><mergeCell ref=\"A").Append(r).Append(":B").Append(r).Append("\"/></mergeCells>");
            sb.Append(SheetEnd());
            return sb.ToString();
        }

        private static void Meta(
            RowBuilder row,
            int r,
            string campo,
            string valor)
        {
            row.Begin(r, 21)
                .Text(1, campo, StyleLabel)
                .Text(2, valor, StyleValue)
                .End();
        }

        private static StringBuilder SheetStart(int frozenRows)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");

            if (frozenRows > 0)
            {
                sb.Append("<pane ySplit=\"").Append(frozenRows)
                  .Append("\" topLeftCell=\"A").Append(frozenRows + 1)
                  .Append("\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            }

            sb.Append("</sheetView></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"18\"/>");
            return sb;
        }

        private static string SheetEnd()
        {
            return "</worksheet>";
        }

        private static string Columns(IEnumerable<double> widths)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<cols>");
            int i = 1;
            foreach (double width in widths)
            {
                sb.Append("<col min=\"").Append(i).Append("\" max=\"").Append(i)
                  .Append("\" width=\"").Append(width.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("\" customWidth=\"1\"/>");
                i++;
            }
            sb.Append("</cols>");
            return sb.ToString();
        }

        private static string PageSetupLandscape()
        {
            return "<pageMargins left=\"0.35\" right=\"0.35\" top=\"0.5\" bottom=\"0.5\" header=\"0.2\" footer=\"0.2\"/>" +
                   "<pageSetup orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"0\" paperSize=\"9\"/>";
        }

        private static string Xml(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            // Remove caracteres de controle proibidos no XML 1.0. Isso evita
            // que nomes/descrições vindos do DWG corrompam o XLSX.
            StringBuilder limpo = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c == '\t' || c == '\n' || c == '\r' || c >= 0x20)
                    limpo.Append(c);
            }

            return limpo.ToString()
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private sealed class RowBuilder
        {
            private readonly StringBuilder _sb;
            private int _row;
            private bool _open;

            public RowBuilder(StringBuilder sb)
            {
                _sb = sb;
            }

            public RowBuilder Begin(int row, double height)
            {
                if (_open)
                    End();

                _row = row;
                _open = true;
                _sb.Append("<row r=\"").Append(row).Append("\" ht=\"")
                   .Append(height.ToString("0.##", CultureInfo.InvariantCulture))
                   .Append("\" customHeight=\"1\">");
                return this;
            }

            public RowBuilder Text(int col, string? value, int style)
            {
                EnsureOpen();
                string cell = CellRef(col, _row);
                _sb.Append("<c r=\"").Append(cell).Append("\" s=\"").Append(style)
                   .Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                   .Append(Xml(value ?? string.Empty))
                   .Append("</t></is></c>");
                return this;
            }

            public RowBuilder Number(int col, double value, int style)
            {
                EnsureOpen();
                string cell = CellRef(col, _row);
                _sb.Append("<c r=\"").Append(cell).Append("\" s=\"").Append(style).Append("\"><v>")
                   .Append(value.ToString("0.###############", CultureInfo.InvariantCulture))
                   .Append("</v></c>");
                return this;
            }

            public RowBuilder FormulaNumber(
                int col,
                string formula,
                double? cachedValue,
                int style)
            {
                EnsureOpen();
                string cell = CellRef(col, _row);
                _sb.Append("<c r=\"").Append(cell).Append("\" s=\"").Append(style).Append("\">")
                   .Append("<f>").Append(Xml(formula)).Append("</f>");

                if (cachedValue.HasValue)
                {
                    _sb.Append("<v>")
                       .Append(cachedValue.Value.ToString("0.###############", CultureInfo.InvariantCulture))
                       .Append("</v>");
                }

                _sb.Append("</c>");
                return this;
            }

            public RowBuilder FormulaText(
                int col,
                string formula,
                string? cachedValue,
                int style)
            {
                EnsureOpen();
                string cell = CellRef(col, _row);
                _sb.Append("<c r=\"").Append(cell).Append("\" s=\"").Append(style).Append("\" t=\"str\">")
                   .Append("<f>").Append(Xml(formula)).Append("</f>")
                   .Append("<v>").Append(Xml(cachedValue ?? string.Empty)).Append("</v>")
                   .Append("</c>");
                return this;
            }

            public RowBuilder NullableNumber(int col, double? value, int style)
            {
                return NullableNumber(col, value, style, StyleText);
            }

            public RowBuilder NullableNumber(
                int col,
                double? value,
                int numberStyle,
                int emptyStyle)
            {
                if (value.HasValue)
                    Number(col, value.Value, numberStyle);
                else
                    Text(col, string.Empty, emptyStyle);
                return this;
            }

            public void End()
            {
                if (!_open)
                    return;

                _sb.Append("</row>");
                _open = false;
            }

            private void EnsureOpen()
            {
                if (!_open)
                    throw new InvalidOperationException("RowBuilder.Begin deve ser chamado antes de escrever células.");
            }
        }

        private static string CellRef(int col, int row)
        {
            return ColumnName(col) + row.ToString(CultureInfo.InvariantCulture);
        }

        private static string ColumnName(int index)
        {
            StringBuilder sb = new StringBuilder();
            int n = index;
            while (n > 0)
            {
                n--;
                sb.Insert(0, (char)('A' + (n % 26)));
                n /= 26;
            }
            return sb.ToString();
        }

        // -----------------------------------------------------------------
        // VALIDAÇÃO DO PACOTE XLSX
        // Não substitui a validação completa do Excel, mas impede os erros
        // que já encontramos: parte ausente, XML malformado e ordem
        // autoFilter/mergeCells incorreta.
        // -----------------------------------------------------------------
        private static void ValidarPacoteXlsx(string caminho)
        {
            string[] partesObrigatorias =
            {
                "[Content_Types].xml",
                "_rels/.rels",
                "xl/workbook.xml",
                "xl/_rels/workbook.xml.rels",
                "xl/styles.xml",
                "xl/worksheets/sheet1.xml",
                "xl/worksheets/sheet2.xml",
                "xl/worksheets/sheet3.xml"
            };

            using FileStream fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.Read);
            using ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

            foreach (string parte in partesObrigatorias)
            {
                ZipArchiveEntry? entry = zip.GetEntry(parte);
                if (entry == null)
                    throw new InvalidDataException("XLSX inválido: parte ausente: " + parte);

                using Stream stream = entry.Open();
                XDocument doc = XDocument.Load(stream, LoadOptions.None);

                if (parte.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase))
                {
                    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                    List<string> ordem = doc.Root == null
                        ? new List<string>()
                        : doc.Root.Elements().Select(x => x.Name.LocalName).ToList();

                    int posFiltro = ordem.IndexOf("autoFilter");
                    int posMerge = ordem.IndexOf("mergeCells");

                    if (posFiltro >= 0 && posMerge >= 0 && posFiltro > posMerge)
                    {
                        throw new InvalidDataException(
                            "XLSX inválido: autoFilter deve aparecer antes de mergeCells em " + parte);
                    }

                    if (doc.Root?.Element(ns + "sheetData") == null)
                        throw new InvalidDataException("XLSX inválido: sheetData ausente em " + parte);
                }
            }
        }
    }

}
