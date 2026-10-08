# Civil3D2026Plugin

Projeto unificado para Civil 3D 2026.2, em **.NET 8 (`net8.0-windows`) / x64**.

## Abrir e compilar

Abra `Civil3D2026Plugin.sln` no Visual Studio e use **Recompilar Solucao**.

A DLL resultante fica sempre em um caminho curto e fixo:

```text
NETLOAD\Civil3D2026Plugin.dll
```

No Civil 3D, execute `NETLOAD` nessa DLL. Ao carregar, o plugin mostra:

- versao geral;
- caminho exato da DLL carregada;
- arvore atual dos modulos e comandos;
- versoes dos modulos versionados;
- aba **C3D Tools** na Ribbon.

Use `C3DHELP` a qualquer momento para repetir a arvore, com descricoes.

## CORRSPLIT v1.2.0 (teste)

Execute CORRSPLIT e selecione o corredor de origem. Na janela em arvore, marque as regioes por baseline ou use Selecionar regiao no desenho,
informe o novo nome e escolha entre Copiar e Transferir. O comando cria so as
baselines necessarias e tenta reproduzir assemblies, frequencias, estacas adicionais,
targets e transitions compativeis. No modo Transferir, remove as regioes da origem
somente apos criar e validar o corredor novo.

A versao atual nao replica Corridor Surfaces, boundaries, overrides e offset
baselines. Consulte Modules/CorridorTools/CorridorSplit/README.md para limitacoes
e roteiro de testes. Teste primeiro em uma copia do DWG e use Copiar.

## Ribbon

A Ribbon e criada automaticamente apos o `NETLOAD`.

Comandos de manutencao:

```text
C3DRIBBON       -> cria/ativa a aba C3D Tools
C3DRIBBONRESET  -> remove e recria a aba a partir do catalogo central
```

Os botoes, grupos e tooltips sao gerados de `Infrastructure/CommandCatalog.cs`.
O primeiro comando de cada modulo e exibido como botao grande e os demais como botoes padrao.

Os icones atuais sao gerados em memoria e nao exigem arquivos externos. A estrutura permite trocar futuramente por PNG/SVG proprios sem alterar os comandos.

## Estrutura

```text
C3DPlugin/
├─ Civil3D2026Plugin.sln
├─ NETLOAD/                     <- DLL pronta para NETLOAD apos compilar
├─ src/
│  └─ Civil3D2026Plugin/
│     ├─ Infrastructure/        <- nomes, versoes, registro, catalogo e inicializacao
│     ├─ Modules/
│     │  ├─ CorridorTools/
│     │  ├─ Drenagem/
│     │  ├─ FeatureLines/
│     │  ├─ Geometry/
│     │  ├─ QTO/
│     │  └─ Surfaces/
│     ├─ Diagnostics/
│     └─ UI/
│        └─ Ribbon/             <- Ribbon e base para futuras interfaces
├─ docs/
└─ tools/
```

## Pontos centrais do projeto

- **`Infrastructure/CommandNames.cs`**: unica fonte dos nomes publicos dos comandos.
- **`Infrastructure/PluginVersions.cs`**: versoes exibidas dos modulos.
- **`Infrastructure/CommandCatalog.cs`**: fonte unica de titulo, descricao, agrupamento e visibilidade na Ribbon.
- **`Infrastructure/CommandRegistration.cs`**: classes que expoem comandos ao AutoCAD.
- **`Infrastructure/PluginApplication.cs`**: unico `IExtensionApplication`; controla banner e inicializacao da UI.
- **`Infrastructure/GlobalUsings.cs`**: aliases padronizados (`AcadDb`, `CivilDb`) para evitar colisao de namespaces.
- **`UI/Ribbon/RibbonUiManager.cs`**: gera a aba C3D Tools a partir do catalogo.
- **`UI/Ribbon/RibbonIconFactory.cs`**: gera os icones atuais.
- **`UI/Ribbon/RibbonCommandHandler.cs`**: envia o clique dos botoes para os comandos normais do Civil 3D.

Consulte `docs/DEVELOPMENT.md` antes de adicionar um novo modulo.


## Passagem semi-elevada (v1.1.0)

Modulo em `Modules/CorridorTools/RaisedCrossing`. A PASSAGEM gera **um unico `Solid3d`** para uma travessia sobre duas vias separadas por canteiro central.

Ordem transversal de selecao:

```text
FL1 externa VIA 1 -> FL2 interna VIA 1 -> FL3 interna VIA 2 -> FL4 externa VIA 2
```

Regras principais:

- cada via recebe `rampa + plato + rampa`;
- o canteiro recebe apenas o plato, sem rampa veicular;
- o conector central inicia depois da extensao interna da VIA 1 e termina antes da extensao interna da VIA 2;
- a base acompanha as cotas das quatro Feature Lines;
- o topo e `base + H`, sendo `H` informado pelo usuario e constante;
- o comprimento informado e apenas o plato;
- o comprimento padrao de cada rampa veicular e 1,00 m;
- a geometria tenta acompanhar as Feature Lines por loft amostrado;
- VIA 1 + canteiro + VIA 2 sao unidos por Boolean Unite em um unico Solid3d.

```text
PASSAGEM            -> cria a passagem
PASSAGEMEDITAR      -> edicao individual
PASSAGEMGRUPO       -> edicao/atualizacao de varias passagens
PASSAGEMATUALIZAR   -> reconstrucao apos alterar Feature Lines
PASSAGEMCFG         -> extensao, rampa e amostragem do loft
PASSAGEMHELP        -> ajuda do modulo
```

O Solid3d final recebe, quando a API AEC estiver disponivel, o Property Set `C3D_PASSAGEM`, com parametros geometricos, handles FL1-FL4, cotas das quatro Feature Lines no CG e larguras VIA1/CANTEIRO/VIA2 no CG. Falha na camada de Property Set nao cancela a geracao geometrica.

## Base oficial de desenvolvimento

A partir da v2.8.0, use esta estrutura como base para todas as novas alteracoes.

Nao crie agrupamentos internos com nomes de tipos Autodesk (por exemplo `Modules.Corridor`).
O agrupamento de ferramentas de corredor e `Modules/CorridorTools`.

Antes de entregar/compilar uma atualizacao, execute `tools/ValidateProject.ps1`.

A DLL de saida continua centralizada em:

```text
NETLOAD\Civil3D2026Plugin.dll
```
