# CLAUDE.md — contexto do Civil3D2026Plugin

Arquivo lido automaticamente pelo Claude Code. Resume o projeto, as regras de trabalho e como
compilar e entregar a DLL. **Leia também `docs/DIARIO.md` (entradas mais recentes no topo) antes de
começar** — é o histórico do que foi feito em cada máquina.

## Quem é o usuário e como falar com ele

- Heyvid (GitHub `heyttor-camelo`), engenheiro civil, usa o Civil 3D todo dia. **Leigo em programação
  e em git**: explique de forma simples, sem jargão.
- **Converse sempre em português.** Textos do plugin (comandos, janelas, avisos) também em português.
- Terminologia: "cota" é a dimensão que mede distância; altura de um ponto é **"elevação"** (use
  "elevação" nas interfaces).
- Ele não quer fazer testes passo a passo. Diagnostique sozinho (log do plugin, scripts, leitura do
  código) e só peça teste no fim, uma vez, com a versão pronta.
- Toda ação em lote que mexa em várias pranchas/viewports/peças precisa de confirmação explicando o
  que vai mudar e de jeito de desfazer (ver `Modules/Layouts/LayoutPanel.Seguranca.cs`). Nomes de botão
  dizem o efeito ("Realinhar", "Reler o painel"), nunca um "Atualizar" ambíguo.

## Regras fixas de trabalho (obrigatórias)

1. **Git: tudo direto no `master`.** Nunca crie branches nem pull requests. Antes de commitar:
   `git pull --rebase origin master` (a nuvem e outros PCs também dão push). Depois: `git push origin master`.
2. **Toda mudança no plugin:**
   - sobe a versão em `src/Civil3D2026Plugin/Infrastructure/PluginVersions.cs` (`Plugin` e a do
     módulo mexido, ex. `Hydro`);
   - ganha uma entrada no topo do `CHANGELOG.md` (formato `## 2.x.y - MODULO vX.Y.Z: resumo`);
   - atualiza o `README.md` se mudou comando, uso ou versão exibida de módulo.
3. **Depois de toda mudança: compilar e lançar a DLL no Google Drive** (seção "Build e entrega" abaixo).
   O Heyvid não usa mais o Visual Studio: quem compila é o Claude, e ele só dá `NETLOAD` na DLL do Drive.
4. Ao terminar um bloco grande de trabalho, acrescente uma entrada em `docs/DIARIO.md` (data, máquina,
   o que foi feito, pendências), commit "Diario: <data> <maquina>" e push.
5. Não mexa no `Pipes Catalog` (`C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog`) sem o aval dele,
   sempre com backup antes e com o Civil 3D fechado.

## Build e entrega

O plugin só compila no Windows com o Civil 3D 2026 instalado (referencia as DLLs da Autodesk).

```powershell
cd <pasta do repo>                       # ex. C:\Users\urgor\source\repos\heyttor-camelo\Civil3D2026Plugin
git pull --rebase origin master
dotnet build Civil3D2026Plugin.sln -c Release -p:Platform=x64
# saída: NETLOAD\Civil3D2026Plugin.dll
```

- **Alvo .NET:** o csproj escolhe net8 ou net10 sozinho lendo `acdbmgd.runtimeconfig.json` do AutoCAD.
  A DLL distribuída deve ser **net8** (carrega em qualquer Civil 3D 2026). Se o Civil da máquina roda em
  .NET 10 (build 25.1.179 em diante, dá CS1705 ou sai net10), compile contra a cópia .NET 8 das DLLs:
  `-p:AcadDir=C:\Users\urgor\source\repos\heyttor-camelo\DeviceModeler\refs\acad2026`
  (pasta fora do git; usada na máquina do trabalho). Detalhes em `docs/DEVELOPMENT.md` §6.
- Alternativa: `tools\Build.ps1 -Configuration Release [-AcadDir ...]`.
- Antes de entregar: `tools\ValidateProject.ps1` (nomes de comando, ponto de entrada único etc.) e,
  se mexeu no cálculo do HYDRO, `dotnet test tests\Hydro.Calc.Tests`.
- Avisos de nulabilidade antigos existem; não crie avisos novos.

**Entrega (sempre, depois de compilar sem erro):**

```powershell
$v   = '2.x.y'                                   # versao de PluginVersions.Plugin
$dll = 'NETLOAD\Civil3D2026Plugin.dll'
$drv = 'G:\Meu Drive\Civil3D2026Plugin'
New-Item -ItemType Directory -Force "$drv\atual", "$drv\versoes\$v", 'C:\Temp\hydro-build' | Out-Null
Copy-Item $dll "$drv\atual\Civil3D2026Plugin.dll" -Force           # e o que ele carrega por NETLOAD
Copy-Item $dll "$drv\versoes\$v\Civil3D2026Plugin.dll" -Force       # copia por versao
Copy-Item $dll "$drv\atual\Civil3D2026Plugin-$v.dll" -Force         # com a versao no nome
Copy-Item $dll "C:\Temp\hydro-build\Civil3D2026Plugin-$v.dll" -Force
# copia da ultima DLL na pasta da FDX (sempre, pedido do Heyvid em 29/10/2026); acha o caminho sozinho
tools\Copy-DllFdx.ps1 -Dll $dll -Version $v
```

- `G:\Meu Drive\Civil3D2026Plugin\atual\Civil3D2026Plugin.dll` é a DLL "oficial", acessível de qualquer
  computador. Se o Civil estiver com ela aberta e a cópia falhar, a versão com número no nome
  (`Civil3D2026Plugin-2.x.y.dll`) resolve: ele carrega a de número maior. Não espere o Civil fechar.
- **Pasta de teste local: só `C:\Temp\hydro-build`.** Não crie `hydro-build2` e afins (confunde ele).
  Mantenha só as ~5 DLLs mais recentes lá e em `atual\` (apague as versionadas antigas).
- **Sempre** copie também a última DLL para `G:\Meu Drive\1 FDX ENGENHARIA\2 - Arquivos de Engenharia\dll`.
  Em PCs onde a pasta da FDX é um atalho compartilhado, ela fica em
  `G:\.shortcut-targets-by-id\<id>\1 FDX ENGENHARIA\2 - Arquivos de Engenharia\dll` (procure a pasta
  `1 FDX ENGENHARIA` dentro de `G:\`). O build Release já faz isso sozinho (`tools\Copy-DllFdx.ps1`,
  alvo `CopyToFdx` do csproj) e só avisa se não achar a pasta.
- Se `G:\Meu Drive` não existir na máquina (Google Drive não instalado/logado), avise o Heyvid.
- No Civil 3D: `NETLOAD` na DLL. Não há instalador/bundle. Ao carregar, o plugin mostra versão,
  caminho da DLL e a árvore de comandos; `C3DHELP` repete a árvore.

## Máquinas do Heyvid

- **CAMELO** (PC de casa): Civil 3D 2026, repo em `C:\Users\urgor\source\repos\heyttor-camelo\Civil3D2026Plugin`.
  Sem Inventor.
- **LAPTOP-11TNS6P8** (notebook do trabalho): Civil 3D 2026.2.2 (13.8.1809), Inventor 2026 + IPE.
- **"not"** (3º computador, usuário Windows `camelo`): Inventor 2026.4, catálogo FDX copiado do Drive.
- Builds do Civil 2026 variam entre máquinas: propriedades da API que mudam entre updates (ex.
  `Catchment.ReferenceDischargeObjectId`) devem ser acessadas por reflexão (ver `Modules/Hydro/CatchmentApi.cs`).
- Confirme o hostname (`$env:COMPUTERNAME`) antes de registrar algo no DIARIO.

## O projeto

DLL única (`Civil3D2026Plugin.dll`) para Civil 3D 2026 (.NET 8/x64), com alvo opcional 2027 (.NET 10,
`-p:Acad=2027`, saída `NETLOAD\2027\`). Aba **C3D Tools** na Ribbon gerada do catálogo central.

### Pontos centrais (`src/Civil3D2026Plugin/Infrastructure/`)

- `CommandNames.cs` — único lugar com os nomes públicos dos comandos (nunca string literal no `[CommandMethod]`).
- `CommandCatalog.cs` — título, descrição, grupo e visibilidade na Ribbon.
- `CommandRegistration.cs` — classes de comando expostas ao AutoCAD.
- `PluginVersions.cs` — versões do plugin e dos módulos.
- `PluginApplication.cs` — o **único** `IExtensionApplication` (banner, UI, serviços de fundo).
- `GlobalUsings.cs` — aliases `AcadDb` / `CivilDb` para evitar ambiguidade de `Entity`, `DBObject` etc.
- `PluginLog.cs` — log comum (`C3DLOG`, Ctrl+Shift+L nas janelas; arquivos em
  `%LOCALAPPDATA%\Civil3D2026Plugin\logs`). Use o log para diagnosticar antes de pedir teste.

Como adicionar comando/módulo: `docs/DEVELOPMENT.md` §2. Padrão de interface para painéis novos:
o do **Viário** (categorias, barra de comandos com ícones, vista gráfica/monitor, lista e abas) —
`docs/DEVELOPMENT.md` §8. Ícones vetoriais em `UI/Common/IconArt.cs`, gerados de `tools/icones`
(estilo de linha tipo AutoCAD, nada arredondado tipo app).

### Módulos (`src/Civil3D2026Plugin/Modules/`)

- **Hydro** (`C3DHYDRO`, foco atual) — apoio à Pipe Network nativa: painel com monitor, edição em lote,
  bacias, cálculo racional/Manning e verificação (`HYCALCULAR`), lançamento (`HYLANCAR`), troca de
  família/tamanho (`HYTROCAR`), edição de medidas da estrutura no desenho (`HYMEDIDAS`, cotas do
  Dynamic Input), regras (`HYREGRA`), aba Peça 3D, tubos levados para dentro das caixas de calçada.
  Lógica pura testável em `Modules/Hydro/Calc` + `tests/Hydro.Calc.Tests`.
  Docs: `docs/HYDRO-PLANO.md` (plano; §8 janela de lançamento e §9 medidas associativas),
  `docs/HYDRO-TESTE.md`, `docs/HYDRO-CATALOGO.md`, `docs/HYDRO-PARTDATA*.md`,
  `docs/CATALOGO-PONTOS-CONEXAO.md`.
- **Viario** (`C3DVIARIO`) — rede viária: eixos, esquinas, hachura em blocos, placas (`C3DVIARIOPLACAS`,
  padrão SinC), atrativos. Tem rede com 800+ vias (Chapada dos Guimarães): cuidado com desempenho.
  Doc: `docs/VIARIO-PLACAS.md`.
- **Layouts** (`C3DLAYOUT`) — explorador de pranchas, séries, numeração, page setup, mapa-chave, norte,
  escala gráfica, ponto de restauração/desfazer. Sensível: ver regra de confirmação acima.
- **Secao** (`C3DSECAO`) — visualizador de seção em janela própria.
- **CorridorTools** — `CORRSPLIT` (dividir corredor), passagem semi-elevada (`PASSAGEM*`), arrays e
  estais (`C3DSOLIDLIGAR*`), `C3DOCULTARPLANTA` (sólidos só no 3D), MF rebaixo.
- **Drenagem** (DrenExcel, DrenNum), **QTO**, **Quadros** (`C3DQUADRORUAS`, xlsx), **PEDs**,
  **FeatureLines**, **Geometry**, **Surfaces**, **Utilitarios**, **LispTools** (`C3DLISP`: LISPs embutidas
  na DLL; as leves vão sendo convertidas para C# com o mesmo nome; QHPE e JPLUS ficam em LISP).
- `C3DMODULOS` escolhe quais módulos aparecem na Ribbon (`%APPDATA%\Civil3D2026Plugin\modulos.txt`).

### Catálogo de peças (Pipe Network)

- Catálogo em uso: **FDX Metric Structures / FDX Metric Pipes** (Country Kit BRA + CSBIM saneado),
  montado por `tools/Merge-PartCatalogs.ps1`; cópia no Drive da FDX (`1 FDX ENGENHARIA\2 - Arquivos de
  Engenharia\Windows\...\FDX-Catalogo-2026-10-21`). Instalar = copiar as pastas para o `Pipes Catalog`
  com o Civil fechado, depois `PARTCATALOGREGEN` e `SETPIPENETWORKCATALOG`.
- **Sempre sincronize com o Drive** depois de qualquer mudança no catálogo FDX instalado: copie as pastas
  `FDX Metric Structures` e `FDX Metric Pipes` do `Pipes Catalog` para a cópia do Drive
  (`...\FDX-Catalogo-2026-10-21`), guardando antes a cópia antiga do Drive como backup (ex. renomear
  para `FDX-Catalogo-2026-10-21-backup-<data-hora>`, na mesma pasta). Pedido do Heyvid em 29/10/2026.
- Scripts em `tools/`: `Ampliar-Faixas-FDX.ps1` (medidas em faixa 1 mm–100 m), `Liberar-Profundidade-FDX.ps1`,
  `Sanitize-CsbimCatalog.ps1`, `Fix-DuplicateColumnIds.ps1`, `Export-PartData.ps1`. Todos fazem backup.
- O caminho das peças de receita do Inventor (_IPE, `tools/inventor/ipe-catalogo/`) foi **abandonado**
  a pedido dele em 28/10/2026. Não retome sem ele pedir.

## Outros arquivos

- `README.md` — uso de cada módulo e comandos. `CHANGELOG.md` — histórico por versão.
- `docs/DIARIO.md` — diário entre máquinas. `docs/DEVELOPMENT.md` — padrões de código, build, interface.
- `docs/REF-PROJECT-EXPLORER.md` — referência do Project Explorer da Autodesk.
- `tools/lisp/INSERECATALOGO.lsp` — insere todas as estruturas de uma Parts List (referência de modelagem).
- `tests/` — `Hydro.Calc.Tests` e `Contorno.Tests` (net8, rodam sem Civil); `tests/viario` (scripts .scr).
