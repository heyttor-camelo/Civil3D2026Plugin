# Diário de trabalho

Resumo do que foi feito em cada sessão, por máquina, para quem continua em outro computador sem o
histórico da conversa. **Entradas mais recentes no topo.** Ao encerrar uma sessão, acrescente a sua.

Máquinas: **trabalho** (Civil 3D 2026 build 25.1.179, .NET 10) · **PC de casa** (2026.2, .NET 8) ·
**notebook** (2026, .NET 8).

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
