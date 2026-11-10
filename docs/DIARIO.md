# Diário de trabalho

Resumo do que foi feito em cada sessão, por máquina, para quem continua em outro computador sem o
histórico da conversa. **Entradas mais recentes no topo.** Ao encerrar uma sessão, acrescente a sua.

Máquinas: **trabalho** (Civil 3D 2026 build 25.1.179, .NET 10) · **PC de casa** (2026.2, .NET 8) ·
**notebook** (2026, .NET 8).

---

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