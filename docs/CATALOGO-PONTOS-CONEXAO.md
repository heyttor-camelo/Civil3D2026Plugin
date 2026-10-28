# Como montar peças do catálogo com pontos de conexão (método validado em 27/10/2026)

Esse método foi validado no Civil 3D 2026 com a BLDC_IPE, a BLSC_IPE e o PV_DNIT_IPE, instalados no FDX Metric Structures, capítulo CSBIM-DRENAGEM. Ele não usa o IPE nem o Part Builder.

## Peças de uma estrutura
- **Recipe** (geometria): pasta `C:\ProgramData\Autodesk\C3D 2026\enu\ContentLibrary\Templates\<2 primeiros>\<resto do id>`. Ela contém:
  - `geom.iam`, com regras iLogic que repassam os parâmetros pelo nome (`UndergroundStructure_USHeight` etc.);
  - 3 `.ipt`: SurfaceStructure, que é a moldura; UndergroundStructure, que é a caixa; e GrateOrCover;
  - `param.xml`, com Value **em cm**;
  - `index.json`, `200.jpg` e `dim.jpg`.
- **Tabela da peça**: `Pipes Catalog\FDX Metric Structures\<capítulo>\<nome>.xml`, com o atributo Recipe e as colunas SBSL, SBSW, WTh, FTh, SRS, SP*, SS*, CG*. Vale usar ColumnRangeList para ter faixa livre.
- **Registro**: a Part dentro do capítulo, no `FDX Metric Structures.apc`, mais os ParameterStrings que faltarem.

## Regras que importam
- Os **pontos de conexão** do Civil vêm dos **Reference Points** dentro do .ipt da caixa. Pipe Connectors e Part Builder não servem para isso.
- O Civil insere pelo centro da moldura, e a moldura encaixa no AssemblyConnector da caixa (`UndergroundStructurePort_wp`). Para a inserção ficar na face da guia, esse work point foi redefinido para (0, −USWidth/2, 0) e o CenterPoint do XML foi ajustado. O `WidthCenterOffset` não serve para isso.
- Nomes padrão obrigatórios: USLength e USWidth (medidas externas), USHeight, WallTh, BaseTh, SPLength/SPWidth ou SPDiameter. O WallTh da caixa fica amarrado ao da moldura.
- Para começar, copie o recipe da Autodesk mais parecido (catálogo "Generic Drainage with Pipe Connections - Metric") com um **id novo**. Edite o .ipt da caixa pela API do Inventor, mantendo os Reference Points.
  - Caixa retangular: `ccf8165e`, com a caixa `a8fe511d` e 9 pontos de referência.
  - PV com chaminé: `ccaec438`, com a caixa `d2eb77e5`.
- A instalação só acrescenta: copia o recipe, copia a tabela e a imagem, e grava a Part no .apc. Faça **com o Civil fechado** e com backup. No notebook, o script genérico é `C:\Temp\BLDC-IPE\install-fdx\Instalar-PecaFDX.ps1 -Recipe -Tabela -Nome`.
- Em outro PC, copie também as pastas de recipe, não só o Pipes Catalog. Quem só usa o catálogo não precisa do Inventor.

## Recipes instalados
- BLDC_IPE: `fcdbd57a-c7c8-4459-86a8-75b3d439f67d`
- BLSC_IPE: `e034d699-3aec-46d6-9475-15afe8c9bbb7`
- PV_DNIT_IPE: `fe685653-c694-456e-ae8c-3d9a20d98f82`

Backups: `C:\Temp\BLDC-IPE\backup-FDX-1726` e `backup-PipesCatalog`. Os catálogos removidos estão em `C:\Temp\BLDC-IPE\catalogos-teste-removidos`.

No NOT (27/10/2026) as três peças foram recriadas com os mesmos ids de recipe, a partir dos scripts de `tools/inventor/ipe-catalogo`. Backup do FDX Metric Structures: `C:\Temp\backup-FDX-Structures-20261027-205645`.

## Parametros editaveis e o que o Civil aceita (testes no NOT, 27/10/2026)
- Cada parametro novo da caixa chega a geometria assim: parametro de usuario `UndergroundStructure_<Nome>` no `geom.iam`, linha na regra iLogic `UndergroundStructure` e `Param` no `param.xml` (cm). Script: `tools/inventor/ipe-catalogo/Add-ParametrosRecipe.ps1`.
- Campos com contexto `Geometry_*` nao aparecem no PartData das pecas de receita. Para o plugin enxergar, use contexto de estrutura (padrao ou proprio, como `StructAlturaCaixa`).
- No NOT, nenhuma peca de receita do Inventor redimensiona: nem as _IPE nem a original Autodesk `f0ebc5ac` (receita `ccf8165e`). Faixa (SIL, SRS) volta ao padrao, e todos os tamanhos saem com o mesmo solido. As Part Builder redimensionam normalmente. Suspeita: o motor de pecas de receita do Civil quebrou com a reinstalacao de componentes do Civil feita pelo instalador do Inventor. Conferir no notebook e reparar o Civil 3D antes de concluir.
- Plano (opcao A): tamanhos por linha na tabela (`New-TabelaIPE.ps1 -Linhas`), AbL/TampaE como colunas com contexto proprio, SRS livre se o motor permitir.

## Parametros por familia (tabelas _IPE)
- BLDC/BLSC/BLTC (boca na guia): SIL, SIW, WTh, FTh, SRS, SFH (marcador), AbL (abertura), AbH, AbVao (BLDC) ou Ab_Gap (BLTC), Aba (aba do fundo), TampaE (laje de topo).
- PV_DNIT: SIL, SIW, WTh, FTh, SRS, AC (chamine), DC (tampao), CB (acrescimo da base do cone), SFH.
- Medidas do Part Builder (Drawing2): aberturas 950 x 150 a 50 do topo; BLDC 2 aberturas com vao 50; BLTC 3 com folga 175; caixa C atras da guia (0 a +800), G do lado da pista (-920 a 0, grelhas 1000 x 520 com 10 fendas 440 x 40, laje 100 abaixo da guia), M dos dois lados (-400 a +800, faixa de grelha 1000 x 335 rebaixada 200). As versoes SUL/CENTRO tem o mesmo solido e viram uma peca so.
- Bocas de bueiro com alas (grupo 3): alem dos pontos da caixa/boca, um ponto de conexao no meio de cada aba (ala), na cota do fundo, conferido com o solido do Drawing2.
- Duplicadas (SUL/CENTRO e outros pares com o mesmo solido) viram uma peca so, com insercao na face lateral se ficam no meio-fio e no centro nas demais.

## Noite de 27 para 28/10/2026 (NOT)
- Grupo 1 montado em `C:\Temp\BLDC-IPE\recipes` (nao instalado): BLDC, BLSC, BLTC (boca na guia), BLDG, BLSG, BLTG (grelha, caixa do lado da pista, insercao na guia = face de tras) e BLDM, BLSM (mista, caixa dos dois lados, guia a 400 da face da pista). Grelha no nivel (a): vaos abertos na laje, sem fendas. Ids das receitas em `ids-grupo1.txt`.
- Volumes da caixa contra o Part Builder: BLDG 1,509 x 1,514; BLSG 0,904 x 0,894; BLTG 2,160 x 2,177; BLDM 1,108 x 1,019 e BLSM 0,703 x 0,637 (as mistas sem o rebaixo de 200 da grelha).
- Tabelas por linha em `C:\Temp\BLDC-IPE\tabelas-grupo1` (`Gerar-TabelasGrupo1.ps1`): SIL de 100 em 100, SIW 600/800/1000 (C), 520/720/920 (G), 1000 (M); AbL acompanha SIL; medidas de projeto como coluna com contexto proprio (StructAberturaComprimento, StructSlabThickness, StructGrelhaComprimento...).
- Instalacao: `Instalar-Grupo1.ps1` (sem `-Executar` so simula). Esperar o diagnostico abaixo antes de instalar.

### Por que as receitas nao regeneram no NOT (so leitura)
- O Civil gera as pecas de receita pelo Inventor Server ("Inventor Interoperability 2026", em `C:\Program Files\Common Files\Autodesk Shared\Inventor Interoperability 2026\Bin`). A instalacao do Inventor em 27/10 atualizou esse motor de 30.10.23300 para 30.40.43500 (1096 de 1347 arquivos com data 27/10, 20:43).
- O registro COM do motor aponta para o Inventor completo, nao para o Interoperability: `Inventor.InventorServer` (CLSID {3FC94EB5-AEBD-4f3f-A2A4-B6CE57113C01}) -> `C:\Program Files\Autodesk\Inventor 2026\Bin\RxAppDocView.dll`, e a typelib do Inventor -> `Inventor 2026\Bin\RxInventor.tlb`. O manifesto do Civil (`AutoCAD 2026\InventorServerClient.manifest`) espera `AutoCAD 2026\Inventor Server\Bin`, que nao existe.
- O controle com a peca Autodesk `f0ebc5ac` foi inconclusivo para tamanhos (os 36 tamanhos da parts list eram a mesma linha, e os campos de tabela sao somente leitura no SizeFilterRecord pela API), mas o SRS dela tambem nao mudou.
- Correcao proposta (precisa de aprovacao e de Administrador): Reparar o "Autodesk Inventor Interoperability 2026" e depois o Civil 3D 2026 (Painel de Controle > Programas), para refazer o registro do motor; depois testar pela interface: Parts List > tamanho maior da familia Autodesk e conferir se o solido muda.

## 28/10/2026 (notebook, LAPTOP-11TNS6P8)
- Motor: aqui o "Inventor Interoperability 2026" nao tem entrada propria em Programas (veio com o Civil 3D 13.8.1809, em 21/10) e os 1372 arquivos continuam de 21/10, versao 30.40.43500; o Inventor nao os sobrescreveu. Mas o registro COM `Inventor.InventorServer` tambem aponta para `Inventor 2026\Bin`, como no NOT. Correcao (Administrador, Civil e Inventor fechados): `RegisterInventorServer.exe` da pasta do Interoperability; se nao bastar, Reparar o Civil 3D 2026.
- As receitas BLDC/BLSC que estavam instaladas aqui eram de antes da correcao: as aberturas da BLDC estavam na parede de tras. O grupo 1 foi refeito do zero em `C:\Temp\BLDC-IPE\recipes` (BLDC/BLSC/BLTC com os ids do NOT; G e M com ids novos, em `ids-grupo1.txt`). Os volumes conferem com os do NOT.
- Dois defeitos achados nos scripts e corrigidos:
  - A checagem do lado da abertura (`Add-BldcFeatures`, `Add-BlscAbertura`, `New-BltcRecipe`) comparava com −USWidth/2 + 5 cm, que e justamente o meio da parede da frente. As faces do corte caem nesse limite, e o resultado (frente ou tras) dependia de arredondamento. Agora o limite e −USWidth/4.
  - Salvar o `geom.iam` roda a regra iLogic, que devolve a caixa aos padroes do iam (os da Autodesk). Rode `Set-PadraoIam.ps1` no fim de cada receita.- Motor no notebook: funciona. Com a peca Autodesk f0ebc5ac, o solido cresce com o Rim to Sump Height (2,000 -> Structure Height 2,150). O registro COM em `Inventor 2026\Bin` nao atrapalha aqui, e nao foi preciso reparar.
- Faixa nas dimensoes nao funciona em peca de receita. A BLDC_IPE com SIL/SIW/WTh/... em ColumnRangeList (uma linha) nao deixou editar Inner Length/Width, e nem o fundo redesenhou. As 138 tabelas de receita da Autodesk (`Geometry_Engine = Ipt`, catalogos Generic Drainage) tem faixa so em SRS, e o resto vem por linha. Vale isso: **dimensoes por linha, SRS em faixa**. A BLDC_IPE voltou a tabela por linhas; a versao em faixa ficou em `C:\Temp\BLDC-IPE\tabela-faixa`.
- Para editar medidas sem escolher linha na mao: o painel HYDRO acha a linha mais proxima do valor digitado e troca a peca (Swap Part pela API).
## Decisao de 28/10/2026: pecas de receita abandonadas
- Diagnostico pela API (comando de teste, no notebook): nas pecas de receita, as colunas por linha (SIL, SIW, WTh, TampaE) vem como `DataSource = BasicTable` e sao somente leitura, tanto no PartData da peca quanto no SizeFilterRecord da parts list ("The property is for read only"). Faixa (`RangeList`) so funciona no SRS, FTh e SFH. Um tamanho criado pela API (`new SizeFilterRecord(fam)` + `AddPartSize`) sai sempre da PRIMEIRA linha da tabela, e foi por isso que no NOT apareceram 36 tamanhos iguais.
- O "tamanho sob demanda" (criar a linha no XML, por em primeiro, PARTCATALOGREGEN, criar o tamanho e trocar) chegou a ser escrito, mas nao foi testado nem commitado. O codigo ficou fora do repositorio, em `C:\Temp\BLDC-IPE\codigo-guardado` no notebook.
- A moldura e a grade da receita Autodesk (SurfaceStructure/GrateOrCover) ficam centradas no ponto de inserção. Com a inserção na face da guia, metade delas fica para fora, como uma placa solta. A modelagem tambem ficou pobre perto do Part Builder.
- Decisao do usuario: largar as pecas de receita e voltar o FDX do notebook ao estado anterior as _IPE. Script: `C:\Temp\Restaurar-FDX-sem-IPE.ps1` no notebook (restaura `C:\Temp\BLDC-IPE\backup-FDX-1726`, que e igual ao FDX atual sem as _IPE, e remove as 9 pastas de receita de Templates, com backup antes). O passo seguinte e o plugin: tubo ligado nas bocas de lobo do CSBIM entra para dentro da caixa (a face da guia e o ponto de inserção dessas pecas).