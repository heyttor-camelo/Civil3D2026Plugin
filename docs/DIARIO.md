# Diário de trabalho

Resumo do que foi feito em cada sessão, por máquina, para quem continua em outro computador sem o
histórico da conversa. **Entradas mais recentes no topo.** Ao encerrar uma sessão, acrescente a sua.

Máquinas: **trabalho** (Civil 3D 2026 build 25.1.179, .NET 10) · **PC de casa** (2026.2, .NET 8) ·
**notebook** (2026, .NET 8).

---

## 2026-11-12 — LAPTOP-11TNS6P8 (Remote Control), 2.109.0: lombofaixa até o topo do meio-fio, grelha vazada, regions, tempo

- Pedidos do Heyvid de 12/11 14h02. Compilado junto com o 4f23ef4 da outra frente (2.108.2, pontos do MFREBAIXO em qualquer ordem, feito na nuvem): compilou de primeira; completei a troca no núcleo (painel/reposicionar) e testei (150,5 → 148,0 gravou 148,00–150,50; mesma estaca recusada).
- Semi-elevada: valor = altura livre final do meio-fio (fundo da sarjeta → topo); a lombofaixa chega no topo do meio-fio (sem a correção de altura). R01-R02: topo do MF x platô −0,1/−0,6 cm; platô sobre a pista 19,7 cm (R01) e 5,2 cm (R02).
- Grelha vazada (quadro + barras 2 cm, vãos 3 cm, um sólido por grelha): 100 barras, 0,034 m³ de aço, área livre 1,39 m² (5 m de plataforma).
- Regions: meio-fio da mesma peça em regions vizinhas juntado (antes ficava só uma). Teste com rebaixos de teste na est. ~205 e a travessia passando da divisa 208,396: sólido 4,29 m³, topo do MF +0,3/+0,1 cm.
- Tempo (log `tempo:`): 49 s → 36 s com rebaixos reaplicados (30 s são Rebuild do corredor); ~4 s quando os rebaixos já estão certos. Desenho 5 s com as grelhas vazadas.
- Entregue 2.109.0 / PassagemRampa 1.12.0 só com versão no nome (Civil aberto); hashes conferidos.
- Pendências: teste do Heyvid; um Rebuild só ao reaplicar os dois rebaixos (exige mexer no MFREBAIXO: a hachura é desenhada depois de cada Rebuild).
## 2026-11-12 — LAPTOP-11TNS6P8 (Remote Control), 2.108.1: viga baldrame no começo da plataforma

- Escolha do Heyvid (12/11): viga baldrame por padrão no encontro rampa/plataforma (`VigaRecuo = -1`, acompanha a rampa); comando com posição [Plataforma/Pe/Medida], painel com caixa + recuo. Travessias já desenhadas não mudam sozinhas.
- Teste (cópia do DWG, `v4\travessia`): R01-R02 5 + 2×1,5 m, vigas 10,10 / 9,57 m, 15 × 27 cm (15 enterrados + 12 dentro da lombofaixa), ~0,41 m³ cada; editar a rampa para 1,0 m moveu as vigas e manteve os handles.
- Entregue 2.108.1 / PassagemRampa 1.11.1 só com versão no nome (Civil aberto); hashes conferidos.
## 2026-11-11 — LAPTOP-11TNS6P8 (Remote Control), 2.108.0: grelha metálica, vigas baldrame, editar sem recriar

- Pedidos do Heyvid depois do teste da 2.106.0 (trava da altura final do rebaixo ficou com a outra frente, 2.107.0 / MFSARJETA v1.25).
- Grelha metálica na sarjeta (padrão, 3 cm, layer `MF-TRAVESSIA-GRELHA`); vigas baldrame 15 x 30 cm nas pontas (15 cm enterrados, parte de cima até o topo da lombofaixa ponto a ponto, recuo editável); editar/redesenhar mantém os handles (`Reuse` + `SwapIdWith`, como no C3DSOLIDLIGAR); MFTRAVESSIA não recusa medidas (avisos); painel com quantidades (grelhas, vigas com seção real, total de concreto) e campos novos.
- Teste (cópia do DWG, `v4\travessia`, `ZZTeste5*.cs` não commitados): R01-R02 5 + 2×1,5 m: platô 7,73 m³, grelhas 4,0 × 0,5 m (3,5 cm livres no R01), vigas 10,24 / 9,56 m com só 0,5–1,6 cm dentro da lombofaixa no pé da rampa; editar para 4 + 2×1,0 m e recuo 0,30 manteve os 52 handles (vigas com 3,8–5,5 cm dentro). Platô de 0,25 m: rebaixo reaplicado acima da altura normal sem erro (o meio-fio só sobe com MFSARJETA v1.25 no corredor; a cópia tem a v1.24).
- Entregue 2.108.0 / PassagemRampa 1.11.0 só com versão no nome (Civil aberto): Drive `atual` e `versoes`, `C:\Temp\hydro-build`, FDX; hashes conferidos.
- Pendências: Heyvid decidir se a viga fica no pé da rampa (quase toda enterrada) ou mais para dentro (recuo); teste dele na tela.
## 2026-11-11 — LAPTOP-11TNS6P8 (Remote Control), 2.107.0: meio-fio com qualquer altura (compilado e testado)

- O commit 038e1b9 (feito na nuvem sem compilar) compilou sem erro nem aviso novo; `ValidateProject` ok.
- Teste sem tela numa cópia nova do DWG da Waldir Soeiro (`C:\Temp\hydro-build\teste-rebaixo\v5-altura\w.dwg`, versão de 11/11 15:14), accoreconsole + `C3DPassagemRampa.dll` com `ZZTeste6.cs` (só na pasta v5-altura, não commitado). Original não aberto; `MFRebaixo\config.json` restaurado.
- O DWG usa o MFSARJETA v1.24 lido de `G:\...\7 - 2026\12 OFICINA BH\MFSARJETA_v1.24.pkt` (`UseEmbeddedProject = False`). Dá para trocar o pkt sem tela: `Subassembly.GeometryGenerator.ProjectOrAssemblyName` + gravar o gerador de volta na peça + Rebuild.
- **Peça antiga (v1.24)**, R01 (normal 13,5 cm) com final 0,18: sem erro, REB −0,045/−0,039 gravado, R01 continua na lista (não vira "removido"), meio-fio fica em 13,5 (peça ignora REB negativo), aviso no log pedindo a v1.25. Rebaixar normal (R03 com 0,02): EXPOSTO 0,020, igual a antes.
- **Peça v1.25** (pkt trocado na cópia): sem mexer em nada, valores iguais à v1.24. R01 com 0,18: EXPOSTO 0,175–0,180 no trecho; com a calçada ligada (na cópia CALCADA estava "Não") a rampa desce 3,4 cm em 0,41 m (8,3%) até a calçada normal, RAMPA_OK 1, FAIXA_LIVRE 1,09. R03 (T30) com 0,35: EXPOSTO 0,350, calçada não cabe a rampa (RAMPA_OK 0, desce reta).
- **Defeito achado e corrigido no gerador**: o HR só esticava a peça quando o exposto passava de H; nas rampas de transição sobravam 9 mm enterrados e o enterrado saltava para EMB no trecho. Agora estica quando o enterrado ficaria menor que EMB e mantém o enterrado da seção normal se ele já era menor (o fundo não sobe). Novo teste R03 0,35: enterrado constante 0,112, H_REAL 0,300 → 0,368 → 0,403 → 0,462 sem salto. pkt gerado de novo (`py gen_mfsarjeta.py`, não há `python` no PATH desta máquina).
- O 0,02 no R01 e o 0,35 no R01 dão "rampa ultrapassa o início da Region" (R01 fica perto do começo da Region): limite que já existia; no painel o rebaixo antigo volta sozinho.
- Entrega com o Civil aberto: só `Civil3D2026Plugin-2.107.0.dll` e `C3DPassagemRampa-1.10.0.dll` (Drive `atual`, `versoes\2.107.0`, `C:\Temp\hydro-build`, FDX `dll`). `MFSARJETA_v1.25.pkt` em `G:\Meu Drive\Civil3D2026Plugin\atual\` e ao lado do v1.24 em `G:\...\7 - 2026\12 OFICINA BH\`.
- Pendências: com o Civil fechado, rodar a entrega de novo (DLL sem número e limpar versionadas antigas); no DWG real, trocar a peça para a v1.25 (o pkt fica ao lado do v1.24) e testar na tela.

## 2026-11-11 — LAPTOP-11TNS6P8 (Remote Control), 2.106.0: menu de travessias no painel e travessias no 3D

- Categoria **Travessias** no painel de rebaixos (`MFRebaixoPanel.Travessias.cs`) e leitura das travessias do desenho para o painel (`MFTravessiaView.cs`: hachuras de planta e sólidos em triângulos via Brep).
- O que tem: lista; Propriedades com edição e **Aplicar e redesenhar**; Redesenhar selecionada/todas, Unir, Separar a união, Apagar (confirmação), Zoom; travessias na planta geral, na planta do rebaixo e no 3D. O painel se atualiza depois dos comandos MFTRAVESSIA*.
- Testado fora da tela (accoreconsole + `ZZTeste5P.cs`, só em `v4\travessia`): R01-R02 com 5 + 2×1,5 m lida com 50 objetos, 2540 triângulos, 7,73 m³; editar para 4 + 2×1,0 redesenhou (5,91 m³); apagar tirou tudo. PNGs em `v4\travessia\painel-trav-*.png`.
- Entregue 2.106.0 / PassagemRampa 1.9.0 completa (o Civil estava fechado): Drive `atual` e `versoes`, `C:\Temp\hydro-build`, FDX; hashes conferidos (74965C180536…, B12B9565B9CC…).
- Pendências: teste do Heyvid na tela (botões, edição, 3D); decidir a tampa onde a sarjeta sobe para o meio-fio (sem espaço para a água).
## 2026-11-11 — LAPTOP-11TNS6P8 (Remote Control), 2.105.3: causa da "lombofaixa que não aparece" + rampa na curva da esquina

- O Heyvid testou às 15:05 (sessão PID 9152, plugin 2.105.1) e a lombofaixa não saiu. Log: `InvalidProgramException` em `MFTravessia.Draw`, 10 s depois de eu sobrescrever `atual\Civil3D2026Plugin.dll` no Drive com a 2.105.2 (15:06:08). No G: o Windows não trava a DLL carregada. Reproduzido o contrário: a mesma 2.105.1 desenha normalmente no `acad.exe` aberto do zero (minimizado, cópia do DWG, com e sem o painel). Os rebaixos dele já tinham sido reaplicados antes do erro.
- Correção na entrega: `tools/CivilAberto.targets` (importado nos dois csproj) e `Copy-DllFdx.ps1`: com `acad.exe` aberto, não troca a DLL sem número nem apaga versionadas antigas. CLAUDE.md atualizado. A 2.105.3 foi entregue assim (só versionadas; a sem número continua a 2.105.2 até a próxima entrega com o Civil fechado).
- MFTRAVESSIA: diz o que ficou gravado (objetos por layer, layer desligada/congelada, caixa do platô) e avisa quando o desenho falha depois de reaplicar os rebaixos.
- Rampa acompanha a curva da esquina (escolha do Heyvid): R01-R02 com 5 + 2×1,5 m agora cabe (7,73 m³, topo no centro 949,851); R01 segue a SARJETA 14, R02 emenda na SARJETA 08.
- Entregue 2.105.3 / PassagemRampa 1.8.3 só com versão no nome (Drive `atual` e `versoes`, `C:\Temp\hydro-build`, FDX), hashes conferidos (634625734025…, BF483AB83062…).
- Próximo: menu próprio de travessias no painel e travessias na vista 3D/planta do painel (pedido do Heyvid).
## 2026-11-11 — LAPTOP-11TNS6P8 (Remote Control), 2.105.2: travessia emenda o meio-fio da baseline vizinha

- O Heyvid avisou que o corredor perto do R01/R02 não acaba, só troca de baseline. Agora a leitura do meio-fio da travessia emenda a outra baseline quando o trecho lido não cobre a travessia (`MFTravessia.ExtendAcrossBaselines`, usa `CurbLines`). Só emenda linha a menos de 1,5 m, no mesmo sentido, e só a parte além da ponta; retorno de esquina fica de fora.
- No DWG (cópia, `v4\travessia`, `ZZT5NEAR` lista o que há perto da ponta): do lado do R02 a SARJETA 08 se sobrepõe 0,40 m à Waldir e foi emendada (1,3 cm de diferença de elevação, sem degrau). Do lado do R01, a ponta (est. 49,106) é o começo da SARJETA 14, que vira para a rua transversal (retorno da esquina). Por isso 5 + 2×1,5 m continua sem caber ali; a mensagem agora explica. Com a emenda, R01-R02 aceita 4 + 2×0,6 m: Solid3d 5,42 m³, topo do MF no eixo +0,9 / −0,5 cm do platô, Refazer igual.
- Entregue 2.105.2 / PassagemRampa 1.8.2 (Drive `atual` e `versoes`, `C:\Temp\hydro-build`, pasta dll da FDX; hashes conferidos: 3D863875FC50…, DF006903BF93…).
- Pendência: decidir com o Heyvid se a travessia R01-R02 deve avançar sobre o retorno da esquina (hoje recusa) ou se ali vale unir as travessias do cruzamento.
## 2026-11-11 — LAPTOP-11TNS6P8 (Remote Control), build e teste da travessia (2.104.0 → 2.105.0 → entregue 2.105.1)

- Compilado o master até 97f9fca (2.105.0, escrito na nuvem): só 1 erro, `Color` ambíguo em `MFTravessia.cs`. 15 avisos antigos, nenhum novo; `ValidateProject.ps1` OK.
- Teste sem o Heyvid numa cópia do DWG da Waldir Soeiro (`teste-rebaixo\v4\w.dwg`, versão de 11/11 13:47), accoreconsole + `C3DPassagemRampa.dll` com `ZZTeste5.cs`/`ZZTeste5P.cs` (só em `v4\travessia`, não commitados). Os comandos reais (`MFTRAVESSIA`, `MFTRAVESSIAREFAZER`, `MFTRAVESSIAUNIR`) rodaram por script, com os cliques passados como coordenadas. O original não foi salvo; `MFRebaixo\config.json` restaurado.
- **Achados e correções (2.105.1 / MFREBAIXO 1.15.1 / PassagemRampa 1.8.1, d50cff9):**
  - o topo do meio-fio não era lido (o MFSARJETA usa GUIA/MF_T30, sem "Curb"), então a elevada não funcionava;
  - no Refazer e na união, os dois lados (ESQ/DIR estão na mesma baseline) pegavam o mesmo rebaixo (aviso falso e altura do lado errado na elevada);
  - o REB é um só no trecho e o MF muda de altura: no R01 o topo ficava 2,3 cm abaixo do platô. Agora o comando confere no eixo e reaplica uma vez compensando (R01 +0,9 cm, R02 −0,5 cm);
  - mensagem com as estacas quando a travessia passa do começo do corredor; Property Set nas tampas dos cantos da união.
- **R01/R02 com 5 + 2×1,5 m não cabe**: o corredor (region TRECHO - ST - 190) começa na est. 49,106 (esquina) e a travessia centrada em 52,05 precisaria de 48,05. O erro é correto. Testado com 4 + 2×0,5: Solid3d de 5,32 m³, largura 9,79 m, topo no centro 949,851, base pela superfície TOPO-WALDYR_SOEIRO_KM_01 (209 pontos), 11 listras + 10 triângulos em Solid3d.
- Pares criados só no teste (R03/R04 na est. ~100, R05/R06 na ~116), semi 5 + 2×1,5 com 0,12: Solid3d 6,37 m³ (8,14 m × 6,5 m × 0,12 ≈ 6,35), elevação ~951,7; tampas 0,25 m³; hachuras plato 40,7 m², rampas 12,4/12,0 m², tampas 2,5 m²; 9 listras + 8 triângulos em Solid3d, apoiados no topo (TopAt = nós da grade, diferença 0,0000). Elevada 5 + 2×1,5 em R05/R06: 5,93 m³. Refazer: nada muda, sem aviso.
- **União** (não há cruzamento utilizável no DWG: perto da est. 49 só a Waldir tem rebaixos; os outros ramos são baselines SARJETA sem rebaixo): testada com 2 travessias vizinhas na mesma rua (est. 100 e 116). Contorno fechado (321 raios), platô Solid3d 15,3 m³, 160,7 m² em planta, 2 tampas nos cantos, cada travessia ficou só com a rampa de fora. O Refazer refaz a união igual e o Separar devolve as 84 peças originais.
- **Painel (4151a9e)**: o link "elevação do topo" aparece nas 5 seções de R01 e R02; clicar no topo mostra "Topo do meio-fio: elevacao 949.602" com ponto e linha, e o segundo clique tira. O texto fica um pouco por cima da cota "topo" (dá para arrastar).
- **Atenção (decisão do Heyvid):** onde a sarjeta sobe para o meio-fio (R01, R04, R06), a tampa de 10 cm com o platô de 12 cm fica **sem espaço para a água** (−3,5 a −6,4 cm). O comando avisa na confirmação. Precisa decidir: tampa mais fina, platô mais alto ou outro detalhe.
- Saídas: `C:\Temp\hydro-build\teste-rebaixo\v4\travessia\` (`saida-*.txt`, `planta-*.png`, `secao-eixo-*.png`, `painel-*.png`, `travessia-*.dxf`).
- Entregue: 2.105.1 e PassagemRampa 1.8.1 no Drive (`atual`, `versoes`), `C:\Temp\hydro-build` e pasta dll da FDX (hashes conferidos: plugin 4ED077F96791…, avulsa 84266B966211…).
- Pendência: teste do Heyvid na tela (cliques reais, união num cruzamento de verdade, tampa x água).
## 2026-11-11 — LAPTOP-11TNS6P8 (Remote Control), build e teste do 2.103.1, elevação corrigida no 2.103.2

- O 694ed31 (2.103.1, nuvem) compilou de primeira (net8, `-p:AcadDir=...\DeviceModeler\refs\acad2026`), sem avisos novos.
- Teste numa cópia nova do DWG da Waldir Soeiro (versão de 11/11 13:47) em `C:\Temp\hydro-build\teste-rebaixo\v4`, accoreconsole + `C3DPassagemRampa.dll` com `ZZTeste4.cs` (só na pasta v4, não commitado). Original não tocado; preferências restauradas.
- **Elevação errada no painel: confirmada.** `CalculatedPoint.StationOffsetElevationToBaseline.Z` é **relativo ao perfil da baseline** (perfil "Grid from DEM ... (66)" da AV. WALDYR SOEIRO LD, ~949,7). Ex. R01 est. 51,233: SOE.Z −0,180 + perfil 949,691 = 949,512 = `cp.XYZ.Z`. `MFRebaixoData.Xyz` jogava fora o Z de `StationOffsetElevationToXYZ`. Corrigido na raiz (2.103.2): Soe/Origins/Points do modelo com elevação absoluta. Centro R01 −0,094 → **949,605** (feature line TOPO_GUIA 949,602, superfície TOPO 949,604); R02 0,220 → **949,962** (TOPO_GUIA 949,947 no meio).
- Seção (cotas): altura/peça/topo/sarjeta nos lugares certos nos dois lados; a cota da sarjeta cortava a base na rampa (R01) e no rebaixado (R02): agora passa abaixo do ponto mais baixo. Grade não encosta mais no cabeçalho. Teste automático: 0 sobreposições, 0 cotas sobre a peça nas 6 seções.
- 3D: ficha sem S0..S3, com "Centro do rebaixo"; ocupa ~25% da largura no canto direito.
- PNGs: `v4\*.png` (depois da correção) e `v4\antes\*.png`; saída `v4\saida.txt`.
- Entregue: 2.103.2 e PassagemRampa 1.6.2 no Drive (`atual`, `versoes\2.103.2`), `C:\Temp\hydro-build` e pasta dll da FDX (hashes conferidos).
- Pendência: teste do Heyvid na tela.

## 2026-11-11 — LAPTOP-11TNS6P8 (Remote Control), build e teste do 2.103.0 / MFREBAIXO 1.13.0

- O f8987e5 (nuvem, sem compilar) **não compilava**: `SelectRebaixo` duplicado (CS0111). Ficou um só (volta para a categoria Rebaixos se preciso e marca na lista). `CalculatedShape.CalculatedLinks` existe e funciona; o resto compilou sem avisos novos (net8, `-p:AcadDir=...\DeviceModeler\refs\acad2026`); `ValidateProject.ps1` OK.
- Teste sozinho numa cópia do DWG da Waldir Soeiro (versão de 10/11 16:55, R01 ESQ e R02 DIR) em `C:\Temp\hydro-build\teste-rebaixo\v3`, accoreconsole + `C3DPassagemRampa.dll` com `ZZTeste3.cs` (só na pasta v3, não commitado). Original não tocado; preferências (`viario_painel.txt`, `MFRebaixo\config.json`) restauradas depois.
- Shapes: 16 por seção em todas as seções (18 no R01, 20 no R02); MF = GUIA/MF_T30 (9 pontos), sarjeta, base, camadas do pavimento. A BASE do MF muda de 4 para 6 pontos em 3 seções (ali vira face, não prisma).
- Corrigido (commit 051a468): **centro do rebaixo ~1 km fora** (centroide com coordenadas UTM perdia precisão); **meio-fio cinza** no 3D/seção (codes da shape são GUIA/MF_T30, sem "Curb"); **perfil sem a parte enterrada** (base só por link "Curb"; agora pelo fundo da shape GUIA); selecionado no 3D acendia em amarelo (vermelho parecia laranja); dicas do 3D sem fundo; chamada da linha d'água sobre a sarjeta; "-0.0" na grade.
- Conferido: tema escuro/claro 4 trocas sem exceção, 3D não duplica, 79 textos e nenhum com contraste < 3; clique no outro rebaixo seleciona na lista sem mexer a câmera; pan/zoom/duplo clique pela mesma conta dos eventos (o mouse em si não dá para simular no console). PNGs em `v3\*.png` (rodadas antigas em `v3\rodada1`, `rodada2`), saída em `v3\saida.txt`.
- Entregue: 2.103.0 e PassagemRampa 1.6.0 no Drive (`atual`, `versoes`), `C:\Temp\hydro-build` e pasta dll da FDX (hashes conferidos).
- Pendência: teste do Heyvid na tela (arrastar de verdade com o mouse, sensação do pan/zoom).

## 2026-11-10 — LAPTOP-11TNS6P8 (Remote Control), build e teste do 2.102.0 / MFREBAIXO 1.12.0

- O 206cd8c (escrito na nuvem sem compilar) compilou de primeira (net8, `-p:AcadDir=...\DeviceModeler\refs\acad2026`), 15 avisos antigos, nenhum novo; `ValidateProject.ps1` OK.
- Teste numa cópia do DWG que o Heyvid salvou às 16:55 no Drive (`...\1 WALDIR SOEIRO\PB_OFICINA_WALDYR_SOEIRO_KM_01_R00_V01.dwg`, com R01 ESQ 50,147–52,613 e R02 DIR 51,878–53,905), em `C:\Temp\hydro-build\teste-rebaixo\v2`, accoreconsole + `C3DPassagemRampa.dll` com comando de teste local (`ZZTeste.cs`, guardado só em `v2\`, não commitado). Original não foi tocado.
- OK: `ShapeCodes` vêm preenchidos (MF_T30 nas duas peças) e a ficha mostra "SUDECAP 30 (MF_T30)"; inclinações 8,33%/9,08% e 8,33%/8,38%; alturas 13,5→11,5 e 23,4→18 cm; coordenadas S0..S3 coerentes com as seções. 3D do trecho colore R01 e R02; clique num rebaixo dá a ficha, fora dá as medidas da face. `GoToSection` troca a seção e marca o Perfil. Corredor com mais de 4000 seções: o "tudo" mostra só o entorno dos rebaixos.
- Corrigido (commit 0bd87a3, ainda 2.102.0): altura final acima das **duas** pontas (o 0,18 que o Heyvid tentou no R01 de 13,5 cm) gravava REB 0 e o rebaixo sumia do painel como "removido" — agora erro claro e o antigo volta; entre as pontas, aviso e cria. Rótulos da Seção para os lados; 3D tudo/enquadrar pela altura da vista; alturas do Perfil ao lado das linhas S0/S3.
- Detalhe visto: o Reaplicar que falha e volta o antigo regrava com a altura medida (0,116 → 0,113), as rampas andam ~3 cm.
- PNGs: `C:\Temp\hydro-build\teste-rebaixo\v2\*.png`; saída em `saida-vistas.txt` e `saida-reap.txt`.
- Entregue: 2.102.0 e PassagemRampa 1.5.0 no Drive (`atual`, `versoes`), `C:\Temp\hydro-build` e pasta dll da FDX (hashes conferidos).

## 2026-11-10 — LAPTOP-11TNS6P8 (Remote Control), build e teste do 2.101.0 / MFREBAIXO 1.11.0 (vistas)

- Compilou de primeira (net8, `-p:AcadDir=...\DeviceModeler\refs\acad2026`), sem avisos novos; C3DPassagemRampa 1.4.0 também; `ValidateProject.ps1` OK.
- Teste numa cópia nova do DWG da Waldir Soeiro (sem salvar; cópia apagada depois), rebaixo criado por `ApplyFromPanel` (128,751–131,751, baseline `1_WALDIR_SOEIRO-Left`, MFSARJETA_DIR), comando de teste local não commitado. `Model(db, r, 6)`: 20 seções, `Soe` do mesmo tamanho de `Points` em todos os 1520 links, `Origins` em todas, `Names` com MFSARJETA_DIR.
- Achado: a "linha d'água" de `CurbOf` pegava o ponto mais baixo dos links Top incluindo a sarjeta, que é a borda da pista (altura 13,3 cm no rebaixado). Corrigido para o pé da face do meio-fio (FLUXO_SARJETA): agora 11,5 cm, igual ao `FinalHeight` (11,48). O 3D abria atrás do meio-fio: câmera passou a vir do lado da pista. Rótulos S2/S3 colados no perfil se sobrepunham: agora descem uma linha.
- Vistas renderizadas fora da tela (janela do painel em -20000, `RenderTargetBitmap`): PNGs em `C:\Temp\hydro-build\teste-rebaixo\vista-*.png`.
- Entregue: 2.101.0 e PassagemRampa 1.4.0 no Drive (`atual`, `versoes\2.101.0`), `C:\Temp\hydro-build` e pasta dll da FDX (hash da Civil3D2026Plugin.dll da FDX = da 2.101.0).

## 2026-11-10 — LAPTOP-11TNS6P8 (Remote Control), build e teste do 2.100.0 / MFREBAIXO 1.10.0

- Compilou de primeira (net8 com `-p:AcadDir=...\DeviceModeler\refs\acad2026`), sem avisos novos; `ValidateProject.ps1` OK.
- O DWG da Waldir Soeiro (versão salva às 12:07) **não tem mais nenhum Transition Set**. Teste numa cópia (`C:\Temp\hydro-build\teste-rebaixo\base.dwg`, sem salvar) com rebaixos criados por `ApplyFromPanel` na region `TRECHO - ST - 190` (baseline `1_WALDIR_SOEIRO-Right`, MFSARJETA_DIR), comandos de teste locais não commitados.
- Resultados: `MFREBAIXOHACHURA` 2x com a layer trancada = "3 apagada(s), 3 desenhada(s)" por rebaixo, sem erro, layer termina trancada, áreas iguais. Remover com outro rebaixo na baseline apaga os 2 sets (4 -> 2); sendo o único da baseline, fica desligado (REMOVIDO). `MFREBAIXOLIMPAR`: com removido + ativo apaga o removido (4 -> 2); só removidos mantém 1 set. `FinalHeight == ExposedAtMiddle` em todos. `MFRebaixoHighlight.ShowLine/Clear` sem exceção no console.
- Entregue: 2.100.0 e PassagemRampa 1.3.0 no Drive (`atual`, `versoes`), `C:\Temp\hydro-build` e pasta dll da FDX.

## 2026-11-10 — nuvem (Claude), rebaixo de meio-fio; build por Remote Control no LAPTOP-11TNS6P8

- Heyvid achou o painel de rebaixos ruim (representação por baseline não ajuda: o meio-fio muitas vezes não está em cima de uma baseline) e mostrou sólidos do corredor com buracos nas transições altura normal -> rebaixada.
- **2.99.0 / MFREBAIXO 1.9.0 / PassagemRampa 1.2.0**: painel refeito em cima do meio-fio real (`MFRebaixoData.Curbs`, faixa borda da pista -> fundo do MF), rampas laranja e trecho rebaixado vermelho; destaque amarelo transiente no desenho ao selecionar (`MFRebaixoHighlight`); campos início/comprimento/fim e rebaixo/altura livre ligados; "Reposicionar no desenho" (`MFRebaixoCommands.TryStationAt`); hachura padrão vermelha sólida 20% só em sarjeta + MF (`HachuraVersao` migra config antigo uma vez); botão que liga/desliga a layer `MF-REBAIXO`. Rebaixos antigos: usar "Redesenhar hachuras".
- **MFSARJETA v1.24**: códigos de shape (S1, S2, S3, S31) deixaram de mudar ao longo da rampa (`Dst` e `MFStatusCode` saíram dos shapes; `MFStatusCode` continua no ponto P7). Hipótese para os buracos: o Civil só liga sólidos entre seções com o mesmo código de shape. **Não confirmado no Civil**: depende de ele reimportar o pkt e reextrair os sólidos.
- **2.99.1 / MFREBAIXO 1.9.1 / PassagemRampa 1.2.1**: no teste dele a hachura não saía, não havia destaque e o rebaixo ficava na baseline. Log (`%LOCALAPPDATA%\Civil3D2026Plugin\logs`): "so 0 secao(oes) com os codes Flange/Back_Curb". A leitura da faixa (`Edges`) usava id da subassembly + codes dos pontos e não achava nada. Refeita em `EdgesBySubassembly`: subassembly pelo nome, bordas pelos codes dos links (`Curb`/`Sarjeta`), interna = origem da subassembly. Planta geral passou a desenhar os corredores inteiros (`PlanStrips`, mesmas faixas da planta do rebaixo). Comando novo `MFREBAIXOHACHURA`. **Lição: ler geometria do corredor pelo caminho que já funciona (links + nome), e conferir no log antes de entregar.**
- **2.99.2**: hachuras saíam em triângulo nas rampas: laço da hachura aberto (o AutoCAD nem calculava a área). Agora fecha o laço (primeiro ponto repetido, `External | Polyline`), manda para a frente e confere área da hachura x polígono no log. Lista sem a coluna de estacas; "Nome no Corridor Transition" nas propriedades.
- **2.99.3**: ícone do rebaixo como rampa de calçada. **Remover** não funcionava nos rebaixos antigos: os Transition Sets (AMF_LIVRE) apontam para `MF_DIR`/`MF_ESQ`, que não existem mais nas assemblies (hoje `MFSARJETA_DIR/ESQ`), e o Civil recusava recriar o set zerado ("Subassembly is failed to find by name"). `MFRebaixoData.Remove` agora apaga de vez o set órfão (o Civil aceita `SetTransitions` sem ele, desde que a lista não fique vazia). Esses 12 rebaixos antigos da Waldir Soeiro não fazem mais efeito no corredor.
- **Teste sem o Heyvid** (funciona): copiar o DWG para `C:\Temp\hydro-build\teste-rebaixo`, `accoreconsole /product C3D`, NETLOAD da `C3DPassagemRampa.dll`, `MFREBAIXOHACHURA`, ler o log. Painel (janela) não abre no console.
- Pendência: teste único do Heyvid (painel, hachura, reposicionar, sólidos com v1.24, Kassel com sarjeta).

## 2026-11-10 — nuvem (Claude) + build no CAMELO (Remote Control)

- **Bug da "troca de viewports" entre pranchas (Layouts)**: era a malha. Copiar um layout leva junto a referência ao mesmo bloco `C3D_MALHA_<handle>` e os itens marcados com a viewport de origem; inserir de novo na origem redefinia o bloco e a cópia passava a mostrar as coordenadas da outra prancha. **2.92.1** (`LayoutConjunto.cs`): bloco só é redefinido se nenhuma outra referência viva o usa (senão `_2`, `_3`...); inserir/remover apaga os itens herdados da cópia (handle de viewport que não está no layout, dono pela posição); aviso no fim listando pranchas que ainda têm itens copiados.
- **2.93.0**: norte e escala entram antes da malha e os textos E=/N= desviam deles; os três ficam num grupo `C3D_MNE_<handle>` (opção "Agrupar", padrão ligado); malha vai para trás (DrawOrderTable.MoveToBottom).
- **2.94.0**: `C3DORDEMDESENHO` pergunta a origem `[Arquivo/Model/Layout]`; painel Geral ganhou "Ordem de desenho do Model ou de outro layout" para os layouts marcados (`DrawOrderCommands.CopiarDoDesenho`).
- **2.94.1**: Renomear em lote com Base vazia + numerar = só prefixo+número+sufixo (antes repetia o nome antigo); título/prévia da aba acompanham a marcação.
- Build no CAMELO: 0 erros, 15 avisos antigos; DLL 2.94.1 no Drive (`atual`, `versoes`), `C:\Temp\hydro-build` e pasta dll da FDX. Pendência: no CAMELO a permissão não deixa apagar as DLLs antigas numeradas (sobrou também uma 2.92.2 de uma compilação paralela descartada); apagar à mão.
- **2.94.2**: folga dos textos da malha ao norte/escala passou para 10 mm (pedido dele).
- **2.95.0**: mapa-chave com `preVia` (caixa em qualquer lugar do layout atual, marcada no XData, fora da atualização normal), lista de pranchas na config (`KeyMapConfig.Layouts`, code 3 no Xrecord; vazia = todas), `DefinirLayouts`/`RemoverDe`/`RemoverPrevias`, menu do botão direito na viewport e botões na aba Mapa-chave. `Gerar` refatorado em `Preparar`/`Desenhar`/`Encaixe`.
- **2.95.1**: caixa própria por prancha (`KeyMapConfig.Caixas`, codes 4/12/13 no Xrecord); `Gerar` detecta MOVE/SCALE na base do mapa (`DetectarMovimento`) e guarda a nova caixa; `C3DMAPACHAVE Mover`, `MoverEm`/`PosicaoPadrao`, itens no menu da viewport e na aba.
- **2.95.2**: bug de origem do mapa-chave: `Adicionar` gravava XData só com o 1001 (regapp), que o AutoCAD trata como "apagar XData" → nenhum mapa tinha marca, Remover achava 0 objetos e cada Gerar podia duplicar. Agora 1000 "mapa"/"previa"; Limpar/Resumo também reconhecem pelas layers `MAPA_CHAVE_*` (LISP e versões antigas). **Lição: XData marcador precisa de pelo menos um valor além do regapp.**
- **2.95.3**: abas do painel com `WrapPanel` no lugar dos `StackPanel` horizontais (botões/campos quebram linha em janela estreita), status com quebra, colunas da lista do mapa-chave mais curtas.
- **2.95.4**: prancha sem viewport sobre a base (capa 000) recebe o mapa-chave inteiro quando escolhida de propósito (`Gerar` só pula quem não foi escolhido); lista da aba Mapa-chave com caixinhas e menu do botão direito (previa / só em / também em / remover / mover; `DefinirLayouts(..., somar)`); `C3DMAPACHAVE Inserir` virou `InserirEm` (só o layout atual, caixa própria), antes refazia em todas.
- **2.96.0**: Heyvid achou a aba Mapa-chave uma bagunça (botões ambíguos). Refeita em três passos (base e lugar / aparência / aplicar). Aparência por layout (`KeyMapEstilo`, `KeyMapConfig.Estilos`, code 6 + 7/42/43/51/64/65/76/77/78 no Xrecord): "Aplicar nos marcados" grava só nos layouts marcados; sem marcados = geral. Lista "só nestes" (code 3) trocada por `Fora` (code 8, mapa tirado) e `Extras` (code 9, incluído mesmo sem viewport), com conversão ao ler. `Encaixe.De(..., titulo)` reserva a faixa do título (texto da folha de cima caía sobre "MAPA CHAVE"). Prévia saiu do painel (palavra `preVia` do comando continua).
- **2.97.0**: mapa-chave com `LerDoLayout` ("Copiar do layout atual": lê hachura, tamanho e posição dos textos do mapa editado à mão; textos marcados no XData com `folha:<layout>` / `titulo`), `KeyMapEstilo.PosTexto` (texto movido, em coordenadas do Model; codes 301+14) e estilos salvos no desenho (`KeyMapConfig.Presets`, code 300; `SalvarPreset`/`ExcluirPreset`).
- **2.98.0**: (a) bug do lugar do mapa: `Gerar` rodava `DetectarMovimento` contra a caixa NOVA, então trocar o lugar pelo painel era lido como "movido à mão" e o lugar antigo era regravado em `Caixas` (achado pelo log no CAMELO). Agora a base leva no XData (`lugar` + 7 reais 1040: posição, escala, caixa) de onde foi desenhada e a comparação é com isso. (b) `LerDoLayout` cria um bloco-molde `C3D_MAPACHAVE_M<n>` com os itens do mapa em coordenadas do Model (`KeyMapEstilo.Molde`, code 305); `Desenhar` clona o molde em vez de gerar contornos/textos/título; `Destaque` (code 79), `Nome` (302). (c) grupo `C3D_MAPACHAVE_<handle do layout>` (`Agrupar`, code 280). (d) coluna "Estilo" no lugar de "Aparência"; botões de lugar renomeados; "Lugar em todas" limpa `Caixas`.
- **2.98.1**: estilo leva o lugar do mapa (`KeyMapEstilo.Caixa`, codes 15/16; `LugarDe`); `Aplicar` grava `Caixas[layout]` (ou o lugar geral, sem marcados) quando o estilo tem lugar; caixinha "Aplicar também o lugar" na aba.
- Aguardando teste do Heyvid de tudo (2.92.1 a 2.98.1).

## 2026-11-09 — nuvem (Claude); build pendente

- Heyvid pediu o último pkt do meio-fio: `subassemblies/MFSARJETA/MFSARJETA_v1.21.pkt` (16/10). Conferido também no LAPTOP-11TNS6P8 (sem versão mais nova).
- **MFSARJETA v1.22**: alvo de offset na flowline (`FLOWLINE_OFFSET`; largura da sarjeta = distância até o alvo, senão `LS`), saída `LS_REAL`. Só o pkt, a DLL não mudou. Ele deixou claro que o comando NÃO deve usar esse alvo.
- Ele mandou um DWG/DXF de sólidos extraídos do corredor (`PB_OFICINA_WALDYR_SOEIRO_KM_01_R00_V01`, 314 MB em `/mnt/project-files/`) reclamando das transições nos sólidos e da visualização dos rebaixos em planta. Lido com `ezdxf` (ACIS/ASM via `ezdxf.acis`): 717 peças, 293 3DSOLID e 424 BODY (cascas), todos os BODY no MFSARJETA; andam em grupos de 6 com a mesma extensão/altura do MF = os links de sobreposição da hachura por code. Mais 8 fatias de 2 mm (seções a +-1 mm de S0/S3).
- **Plugin 2.92.0 / MFREBAIXO 1.8.0 / PassagemRampa 1.1.0** (não compilado na nuvem, sem dotnet/Autodesk): hachura do rebaixo desenhada pelo plugin (`MFRebaixoHatch.cs`, layer `MF-REBAIXO`, XData `C3D_MFREBAIXO`), painel com Redesenhar hachuras e Configuração da hachura, sem seções de +-1 mm. **MFSARJETA v1.23**: sem links de sobreposição, rampa sempre `Calcada`, Kassel com sarjeta (pedido dele: faces alinhadas na troca T30/T40 -> Kassel).
- Pendências: compilar no PC (Release x64 net8), ValidateProject, entregar nas pastas do Drive/FDX; Heyvid testar MFREBAIXO + v1.23 num desenho (hachura, sólidos sem BODY, Kassel com sarjeta no Composer). Depois: reformular o comando de rebaixo como ele quiser (ainda não disse como).

## 2026-11-07 — nuvem (Claude) + CAMELO (build)

- Pedido do Heyvid: DLL à parte só com a passagem semi-elevada e a transição de rampa (Corridor Transition) → entendido como RaisedCrossing (`PASSAGEM*`) + MFRebaixo (`MFREBAIXO*`).
- Novo projeto `src/C3DPassagemRampa` (`C3DPassagemRampa.dll`, v1.0.0, plugin 2.91.0) na mesma solução. Não duplica código: compila por link os arquivos dos dois módulos + GlobalUsings, CommandNames, PluginVersions, PluginLog (`#if PASSAGEM_RAMPA` tira o CommandCatalog) + UI/Common (ToolTheme, ToolStyles, ToolIcons, IconArt, LogView). Ponto de entrada próprio (`PassagemRampaApplication`, comando `C3DPASSAGEMRAMPA` lista os comandos). Plugin completo e DLL avulsa avisam se carregados juntos.
- Entrega igual à do plugin (targets no csproj): `atual\C3DPassagemRampa.dll` (+ versionada), `versoes\C3DPassagemRampa\1.0.0`, pasta `dll` da FDX (`Copy-DllFdx.ps1 -Name`), `C:\Temp\hydro-build`.
- Build no CAMELO (Release x64, net8): faltava um `using` no projeto novo (commit a4103ab); sem avisos novos. ValidateProject OK. Todas as cópias conferidas no Drive e na FDX.
- Pendência: Heyvid testar `NETLOAD` da `C3DPassagemRampa.dll` sozinha (sem a Civil3D2026Plugin.dll) num desenho com passagem/rebaixo.

## 2026-11-01 — nuvem (Claude) + CAMELO (build)

- **Viario 0.13.0 / plugin 2.88.0** (commit `1a47d77`): revisao do `C3DVIARIOCIVIL` depois das mudancas
  da planta (cache por eixo, atualizacao manual, hachura em blocos, placas). A geracao em si (eixos,
  bordos LD/LE offset, curb returns) nao tinha quebrado; ajustes foram no caminho ate ela:
  Gerar com rede pendente oferece "Atualizar rede" antes; `Gerar [Tudo/Selecionar]` (escopo gravado
  no NOD, chave `CIVILESCOPO`, fora da assinatura da rede); acompanhar a rede mais leve (sem reconferir
  curb returns mantidos, nomes lidos so ao criar, cache de ids para o painel); `U` depois do Gerar
  relê o estado; botoes do explorador renomeados; `DebugWidening` desligado.
- Build Release x64 na CAMELO: net8, 0 erros, 9 avisos antigos de nulabilidade; ValidateProject OK.
  DLL em `atual\`, `versoes\2.88.0`, `C:\Temp\hydro-build` e pasta dll da FDX.
- **Viario 0.14.0 / plugin 2.89.0** (mesma noite, pedido do Heyvid): etapa 2 = perfis. `C3DVIARIOCIVIL > Perfis`
  (`ViarioCivilProfiles.cs`): terreno (superficie escolhida) + greide por layout no eixo; alinhamento offset
  novo por lado no **bordo do pavimento** (nivel Pista, `PAV LD/LE`, so com sarjeta) com perfil offset
  dinamico ligado ao greide pela declividade transversal (padrao 3%). A sobrecarga `Profile.CreateOffsetProfile*`
  nao foi conferida na nuvem (PC fora da ponte): e chamada por reflexao, com fallback calculado pelo plugin
  (PVIs do greide projetadas no bordo, refeitas quando o greide muda). Opcoes no NOD `CIVILPERFIS`.
- **2.89.1**: a API real (AeccDbMgd 13.8.1516, lida por metadados na CAMELO) e o metodo de instancia do perfil pai
  `Profile.CreateOffsetProfileBySlope(string profileName, ObjectId offsetAlignmentId, ObjectId profileStyleId, double slope)`
  (nao ha estatico `CreateOffsetProfile*`); `OffsetProfileParameters.Stations` (lista de Station/Slope/Description) permite
  declividade por estaca, ainda nao usada. `ProfilePVI.Station` esta obsoleta: usar `RawStation`. Build na CAMELO: net8,
  0 erros, 9 avisos antigos; DLL 2.89.1 em atual\, versoes\, hydro-build e dll da FDX.
- **Viario 0.15.0 / plugin 2.90.0** (02/11, pedido "quero copiar o dwg, e da uma revisada no processo de hachuras"):
  novo `C3DVIARIOCOPIAR` (`ViarioTransfer.cs`): WblockCloneObjects dos eixos para o ModelSpace do outro desenho aberto,
  reaplica XData (EIXO/NOME) via mapa de handles, mescla secoes (iguais reaproveitadas; conflito de id vira copia),
  RAIOS/LIVRES/SOLTAS remapeados pela chave de esquina, ILHASOFF/ILHARAIO mesclados, CONFIG/AUTO so se o destino
  nao tinha rede; `ViarioSync.Forget(destino)` para a rede ser refeita la. Hachura revisada: pipeline estava certo;
  corrigidos ordem de desenho (MoveToBottom das hachuras novas), avisos do acompanhar-rede que sumiam (agora C3DLOG +
  resumo), guarda de XData vazia, texto do aviso de contorno aberto. Build Release x64 na CAMELO: net8, 0 erros, 9 avisos antigos;
  DLL 2.90.0 em atual\, versoes\2.90.0, hydro-build e dll da FDX.
- **2.90.1**: teste do Heyvid do `C3DVIARIOCOPIAR`: painel certo no destino, planta sem as linhas novas. Causa: no
  `ObjectAppended` do clone (WblockCloneObjects) a XData ainda nao e legivel, o eixo nao era marcado pendente e o Idle
  nao refazia a rede. Agora `ViarioSync.NotifyImported(db, ids)` registra os eixos e, com automatico ligado, a rede e
  refeita no destino dentro do proprio comando (`Regenerate(dest, quiet, force)` sob LockDocument).
- **2.90.2 / 2.90.3** (02/11 noite): Heyvid moveu os eixos no destino e "apareceu tudo" (leitura dos eixos do zero). 2.90.2:
  `NotifyImported` zera o estado do destino (cache, varredura) antes de refazer; linha DIAG no log a cada rede refeita;
  botao do painel "Atualizar" -> "Reler o painel". 2.90.3: o log mostrou duas copias para o mesmo DWG (18:42 e 18:48) -
  o copiar agora pula eixo com a mesma geometria ja presente no destino (`AxisSignature`). Builds na CAMELO.
- **2.90.4 / 2.90.5**: hachura "vazando" (cunhas com lado reto) no DWG de destino. Montado `tests/Viario.Engine.Tests` (motor +
  hachura puros, por link) e `ViarioDump` (retrato JSON gravado pelo HACHURA > Gerar em %LOCALAPPDATA% e no Drive `diag\`).
  Replay do retrato real na CAMELO: 19 nos, 60 linhas, 47 esquinas, 9 lacos, **nenhuma cunha** - o vazamento foi da copia dupla
  (26 eixos sobrepostos), nao do contorno. 2.90.5 limpa chaves de raio orfas (o retrato tinha 123 raios para 41 esquinas).
  Builds 2.90.4 e 2.90.5 na CAMELO: 0 erros, 9 avisos antigos; DLLs em atual\, versoes\, hydro-build e dll da FDX.
  Lembrete: o `Copy-DllFdx.ps1` tenta apagar versoes antigas e a exclusao e bloqueada na sessao remota (copias feitas a mao).
- **Pendente**: teste no Civil (roteiro: C3DVIARIOCIVIL > Gerar > Tudo numa rede pequena; Selecionar em
  rede grande com atualizacao manual; `U` depois do Gerar; `Perfis` com uma superficie; `C3DVIARIOTESTE`).
  Conferir no `C3DLOG` se o offset dinamico saiu ou se caiu no calculado ("Perfil offset nao criado").
  Fora: perfis dos curb returns e amarracao dos greides nos cruzamentos.

---

## 2026-10-21 — CAMELO (Civil 3D R25.1.60, .NET 8 e 10)

- `git pull` do master (c8990b8 → 9ef223d). `dotnet build -c Release` OK: 0 erros, 9 avisos de
  nulabilidade (`RaisedCrossingCommands.cs`, `ViarioCivilPlan.cs`, `ViarioCivil.cs`); DLL em
  `NETLOAD\Civil3D2026Plugin.dll`. `Hydro.Calc.Tests`: 19/19.
- `docs/HYDRO-PARTDATA.md`: medidas que o PartData expõe por família (nome, descrição, contexto,
  unidade, faixa/lista) do CSBIM e do Metric Structures instalados aqui, gerado por
  `tools/Export-PartData.ps1` (novo, só leitura).
- Nesta máquina o **FDX Metric Structures não está instalado** e o CSBIM em `Pipes Catalog` é o
  original, **não saneado** (com `.bak`/`.dwl` e listas). A pasta do Drive com o FDX não está
  sincronizada aqui.

---

## 2026-10-21 — trabalho

### Plugin / HYDRO
- Módulo **HYDRO** criado do zero a partir de `docs/HYDRO-PLANO.md`, fases H0–H5, em commits
  `e2d470b` … `0f181e7` (plugin 2.61.0, Hydro 0.6.0): painel `C3DHYDRO`, edição em lote da Pipe
  Network, bacias, racional/Manning/verificação, `HYLANCAR` em cadeia, `HYCATALOGO` e parâmetros de
  faixa do catálogo. 19 testes verdes, `ValidateProject` OK. **Nada testado dentro do Civil 3D**
  (roteiros: `docs/HYDRO-TESTE.md`, `docs/HYDRO-CATALOGO.md`). Alvo 2027 ficou fora do escopo.
- **Release v2.61.0** no GitHub com a DLL pronta para `NETLOAD` (compilada para .NET 8; carrega
  também no 2026 que roda em .NET 10). README ganhou a seção "Baixar pronto".
- **Build nesta máquina:** o Civil 3D instalado é .NET 10 e o csproj mira .NET 8, então compile com
  `-p:AcadDir=C:\Users\urgor\source\repos\heyttor-camelo\DeviceModeler\refs\acad2026` (cópia .NET 8
  das DLLs da Autodesk, fora do git). Em casa, `dotnet build` normal.

### Catálogo de peças (Pipe Network)
- Análise do catálogo **CSBIM** (53 estruturas, 23 tubos): bocas com medidas travadas (listas
  fechadas e independentes), contextos da Autodesk usados fora do significado, listas ocultas e
  arquivos órfãos. Detalhes em `tools/Sanitize-CsbimCatalog.ps1` (cabeçalho) e no relatório gerado.
- `tools/Sanitize-CsbimCatalog.ps1` aplicado (listas → faixas, contextos → `Geometry_ModelParameter`
  nas bocas, DN 700/900, quarentena). Depois, um reset do Civil 3D apagou os catálogos custom e os
  backups; o CSBIM saneado foi restaurado a partir do Drive.
- `tools/Merge-PartCatalogs.ps1` criou **FDX Metric Structures** (251 peças = Country Kit BRA +
  CSBIM saneado, capítulos `CSBIM-*`) e **FDX Metric Pipes** (91), em
  `C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog\`. Originais intactos.
- **Drive** (`G:\Meu Drive\1 FDX ENGENHARIA\2 - Arquivos de Engenharia\Windows\c0ca45ed-…\`):
  `CSBIM-saneado-2026-10-21\` e `FDX-Catalogo-2026-10-21\`, cada uma com LEIA-ME de instalação.

### Civil 3D (instalação)
- O 2027 foi cancelado (sem extensão IFC 4.3). A reinstalação do 2026.2 deixou os pacotes de idioma
  na versão antiga (13.8.280) com módulos 13.8.1809 → crash no Hydrology ao abrir. Um reset/reinstalação
  completo resolveu (módulos e recursos em 13.8.1809). O Country Kit BRA 2026 voltou limpo.

- 2.61.1: csproj passa a escolher net8/net10 pelo runtime do Civil 3D instalado (o Visual Studio
  desta maquina dava CS1705); `MenuItem` fixado no HydroContextMenu. `docs/DEVELOPMENT.md` par. 6.

### Pendente
1. No Civil 3D: `PARTCATALOGREGEN`; `SETPIPENETWORKCATALOG` → FDX Metric Structures/Pipes.
2. Testar HYDRO com `docs/HYDRO-TESTE.md` e `docs/HYDRO-CATALOGO.md`; corrigir o que aparecer.
3. Testar as peças CSBIM saneadas (boca `CSBIM-ALAS\BDCC`, `CSBIM-DRENAGEM\PV DNIT`).
4. Instalar o catálogo FDX nas máquinas de casa (LEIA-ME no Drive).

---

## 2026-10-22 — sessao na nuvem (Claude), sem Civil 3D

- **Viario v0.11.0 / plugin 2.70.0**: novo comando `C3DVIARIOPLACAS` (placas de sinalizacao vertical no
  padrao do SinC) em `Modules/Viario/ViarioPlacas.cs` + `ViarioPlacaSync.cs`; categoria **Placas** e botao com
  menu no explorador; entrada `PLacas` no `C3DVIARIO`; catalogo, Ribbon, README, CHANGELOG e `docs/VIARIO-PLACAS.md`.
- Origem: projeto de sinalizacao turistica de Chapada dos Guimaraes (Volume I-C, 121 placas, 15 trechos),
  extraido do PDF para `placas_chapada.json` / `placas_projeto_x_sinc.csv` (em `C:\sinC\Local\Placas\Placas 2D\_PROJETO\`),
  casado com os 118 arquivos `T-xxx` do SinC. A estrutura dos blocos/atributos foi lida de um DXF gerado pelo SinC.
- **Nao compilado nem testado no Civil 3D** (sem DLLs da Autodesk na nuvem). Sintaxe conferida; API usada e a mesma
  dos modulos vizinhos (PedBlock, ViarioSignageSync, ViarioStore).

### Pendente
1. Compilar (`dotnet build -c Release`, na maquina de trabalho com `-p:AcadDir=...`), `tools/ValidateProject.ps1`, NETLOAD.
2. Testar `C3DVIARIOPLACAS > Importar` com `placas_chapada.json` no DWG das vias de Chapada; conferir no SinC se
   ele reconhece as placas inseridas (atributos/handles) e o formato que ele espera em Nort/Est.
3. Conferir rotacao/lado das placas em campo (a direcao vem dos eixos; sem eixo perto, a placa fica sem vinculo).

- (mesma sessao, depois) suportes pela area, prefixo N:/E:, importacao direta da pasta do SinC (`PAsta`), monitor da
  placa no explorador (desenho da placa + planta com nomes dos eixos) e **atrativos turisticos** (`C3DVIARIOATRATIVOS`,
  KMZ da prefeitura: 105 pontos em 5 pastas). Tambem sem compilar.

---

## 2026-10-28 - notebook (LAPTOP-11TNS6P8)

- Pecas de receita (_IPE): grupo 1 montado e instalado, depois abandonado a pedido do usuario. O FDX
  voltou ao estado de 27/10 17:26; os arquivos ficaram em `C:\Temp\_arquivo-catalogo-ipe-20261028`.
  Aprendizado em `docs/CATALOGO-PONTOS-CONEXAO.md`.
- HYDRO 0.12 a 0.16 (plugin 2.79.0 a 2.83.0):
  - Tubos levados para dentro das bocas de lobo de calcada (base point na face), no automatico, com
    botao no painel.
  - Medidas da peca refazem o solido: o setter do PartData nao redimensiona, entao a peca e trocada
    para um tamanho da parts list com os valores novos.
  - HYMEDIDAS (cotas dinamicas no desenho), aba Peca 3D no painel, nomes em portugues (HydroNames),
    HYREGRA em portugues.
- Nada testado no Civil nesta sessao: o accoreconsole nao carrega DLL de pasta nao confiavel.
- DLL de teste em `C:\Temp\hydro-build`.

### Pendente
1. Rodar `C:\Temp\Ampliar-Faixas-FDX.ps1` (Civil fechado) para medidas livres e depois PARTCATALOGREGEN.
2. Testar no Civil: edicao de comprimento/largura (troca de tamanho), HYMEDIDAS, aba Peca 3D e os tubos dentro.