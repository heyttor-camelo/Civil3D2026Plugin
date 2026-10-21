# HYDRO — catálogo de peças CSBIM (fase H5)

O HYDRO trabalha com os objetos nativos da Pipe Network; as peças (PVs, bocas, alas, tubos) vêm
do catálogo Part Builder do Civil 3D. O escritório usa dois catálogos CSBIM, instalados em
`C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog\`:

| Pasta (GUID do catálogo) | Domínio | Capítulos |
|---|---|---|
| `U3929348-IRIWUEH-393J4J2-DIJFI` | Estruturas (53 peças) | ACESSIBILIDADE, ALAS, DRENAGEM, ELÉTRICA, ESGOTO, Simple Shapes |
| `93920319-9R9OIJSD-OI3NJ3-IJFOE` | Tubos (23 peças) | CIRCULAR, RETANGULAR, CANALETA, ADS_TIGRE, GUARANI, ELETRICA |

Os `.apc` dos dois usam os mesmos ids de catálogo dos catálogos métricos da Autodesk
("Metric Structures"/"Metric Pipes"); o que distingue um catálogo do outro é a pasta e os
GUIDs das peças.

## 1. O que o saneamento faz (`tools/Sanitize-CsbimCatalog.ps1`)

Só os `.xml` das famílias mudam; os `.dwg` (modelos das peças) ficam como estão.

- **Listas viram faixas**: parâmetros dimensionais (float, mm) que eram listas fechadas
  (`ColumnConstList`) passam a faixas (`ColumnRangeList` com Min/Max/Default). O Civil aceita
  qualquer valor dentro da faixa, em vez dos 5 ou 6 valores pré-gravados. Ficam como lista o
  diâmetro de tubo (`PipeInnerDiameter`), o diâmetro de tampão (`StructFrameDiameter`) e o
  Manning, que são itens discretos de mercado.
- **Contextos das bocas** (`Struct_InletOutlet`, alas e bocas de bueiro): parâmetros que usavam
  contextos da Autodesk fora do significado (`StructInnerWidth` = "espessura das paredes",
  `StructVertPipeClearance` = "altura da parede menor"...) passam a `Geometry_ModelParameter`.
  Só `StructInnerDiameter` fica, porque é o diâmetro do tubo de fato. Sem isso o Civil mostrava
  "largura interna" e "folga vertical" com valores sem sentido nas propriedades da estrutura.
- Listas ocultas com mais de um valor ficam visíveis; listas ordenadas e sem repetição;
  DN 700 e 900 incluídos nas listas de diâmetro de tubo.
- **Quarentena**: `.bak`, `.dwl`, `.dwl2`, `*_recover.dwg`, `.dwg` sem `.xml` e `TESTE_*` vão
  para `<catálogo>\_quarentena\<capítulo>\` (nada é apagado).
- Backup de cada catálogo em `_backup_<GUID>_<data>.zip` e relatório em
  `_saneamento_CSBIM_<data>.md`, os dois na pasta `Pipes Catalog`.

Aplicado em 21/10/2026 15:05 nesta máquina (207 faixas, 79 contextos, 33 listas expostas,
111 arquivos em quarentena).

## 2. Repetir em outra máquina

```powershell
# 1. Só simular (imprime o plano, não muda nada)
powershell -ExecutionPolicy Bypass -File tools\Sanitize-CsbimCatalog.ps1

# 2. Aplicar, com backup .zip e relatório
powershell -ExecutionPolicy Bypass -File tools\Sanitize-CsbimCatalog.ps1 -Apply
```

Depois, no Civil 3D:

1. `SETPIPENETWORKCATALOG`: pasta `C:\ProgramData\Autodesk\C3D 2026\enu\Pipes Catalog`,
   catálogo de estruturas `U3929348-IRIWUEH-393J4J2-DIJFI`, de tubos
   `93920319-9R9OIJSD-OI3NJ3-IJFOE`. (`HYCATALOGO` > `Definir` abre esse diálogo.)
2. `PARTCATALOGREGEN` para reindexar os `.apc` (`HYCATALOGO` > `Regenerar`).
3. `HYCATALOGO` > `Importar` para montar a parts list "CSBIM" (ver §3).

Para voltar atrás: descompacte o `_backup_<GUID>_<data>.zip` por cima da pasta do catálogo e
rode `PARTCATALOGREGEN`.

## 3. O que o HYDRO faz com o catálogo

- **`HYCATALOGO`** lista os catálogos da pasta (domínio, nome, peças, capítulos, se é CSBIM e se
  está saneado) e marca os ativos no desenho. Como a API 2026 não expõe o caminho do catálogo
  ativo, o ativo é reconhecido comparando os GUIDs de `PartsList.GetAvailablePartFamilies`
  (as famílias do catálogo ativo) com os `.apc`; `Definir` manda o comando nativo
  `SETPIPENETWORKCATALOG`, `Regenerar` manda `PARTCATALOGREGEN` e `Importar` cria/usa uma parts
  list e importa as famílias dos capítulos escolhidos com `PartsList.AddPartFamilyByGuid` e os
  tamanhos com `PartFamily.AddPartSize(SizeFilterRecord)`: um tamanho por valor da lista de
  diâmetro (tubos, bocas com `StructInnerDiameter` em lista) ou um tamanho com os padrões
  quando a peça só tem faixas. Famílias já presentes são puladas; o que falhar é listado.
- **Parâmetros de faixa por estrutura**: a aba Propriedades de cada estrutura mostra os
  parâmetros numéricos da peça que vêm de faixa ou lista (`Part.PartData` →
  `PartDataField` com `IsFromRange`/`IsFromList`), com a faixa Min..Max e a unidade. Editar e
  Enter grava via `PartDataField.Value` + `Part.PartData = registro` (os dois têm setter na
  API 2026), validando antes no Min/Max; valores fora da faixa são recusados. Em lote
  (estruturas marcadas), aparecem os parâmetros comuns a todas; vazio = mantém. Um UNDO por
  operação, como o resto do painel.
- **Lançamento** (`HYLANCAR`): a família/tamanho escolhida vira o padrão do desenho
  (Calculo > "Lancamento: estrutura/tubo padrao", formato `Familia | Tamanho`) e é proposta
  na próxima vez; com a parts list CSBIM na rede, as famílias oferecidas são as dela.
- **Aviso no painel**: em Redes (propriedades da rede) e na barra de status aparece
  "CATALOGO: ..." quando a parts list da rede não tem famílias, quando alguma família não vem
  dos catálogos CSBIM (pelo GUID) ou quando o catálogo CSBIM desta máquina não está saneado
  (sem `_saneamento_CSBIM_*.md` e `_quarentena`); o botão "Catalogo CSBIM" abre o `HYCATALOGO`.

## 4. Roteiro de teste no Civil 3D (H5)

1. `HYCATALOGO` num desenho novo do template métrico: a lista deve mostrar "BRA Metric
   Structures/Pipes" como ativos e os dois CSBIM como "CSBIM saneado".
2. `HYCATALOGO` > `Definir`: no diálogo, escolha os dois catálogos CSBIM. `HYCATALOGO` >
   `Regenerar`. De novo `HYCATALOGO`: agora os CSBIM aparecem como "ATIVO".
3. `HYCATALOGO` > `Importar` > `Ambos` > parts list `CSBIM` > capítulos `2,3` (ALAS, DRENAGEM)
   e, para tubos, `1` (CIRCULAR). Conferir no Toolspace > Settings > Pipe Network > Parts
   Lists > CSBIM: as famílias e os tamanhos (um por diâmetro nos tubos). A linha de comando
   diz quantas famílias/tamanhos entraram e lista as falhas.
4. `HYLANCAR` numa rede com a parts list CSBIM (crie pelo painel > Redes > Nova rede ▾ >
   CSBIM): as famílias oferecidas são as CSBIM; a escolha fica salva (Calculo > Lancamento).
5. Painel > Estruturas > um PV DNIT: em "Parametros da peca" devem aparecer SIL/SIW/AC/SRS
   com a faixa; mude SIL para 1800 e Enter: a peça muda no desenho (Structure Properties >
   Part Properties). Digite 99999: recusado com a faixa na barra de status.
6. Uma boca (BDCC): os parâmetros SVPC, SHBW, SHBTh, SIL, SID, SCH, SSTh, SBPC, SBH, SIW
   aparecem com os nomes em português e as faixas do saneamento.
7. Marque três PVs: "Parametros da peca comuns" lista só os que existem nos três; preencha
   SIW = 1500 e "Aplicar nos marcados": os três mudam.
8. Troque a parts list da rede para uma da Autodesk (Network Properties): o painel passa a
   mostrar "CATALOGO: N de M familia(s) ... nao vem do catalogo CSBIM" e o botão abre o
   `HYCATALOGO`.

## 5. O que não pôde ser verificado fora do CAD

- Se `PartsList.GetAvailablePartFamilies` devolve só o catálogo ativo (base da detecção) e se
  `AddPartFamilyByGuid` aceita o GUID da peça com os `.apc` ainda não regenerados.
- Se `PartFamily.AddPartSize(SizeFilterRecord)` com só o diâmetro preenchido cria o tamanho
  (os outros campos ficam no padrão do catálogo) e se a chamada repetida com o mesmo valor
  duplica tamanhos.
- Se `Part.PartData = registro` com um `Geometry_ModelParameter` alterado regenera o sólido da
  peça no desenho, ou se o Civil exige trocar o tamanho (`SwapPartFamilyAndSize`) para peças
  cujo parâmetro define o modelo.
- O diálogo nativo `SETPIPENETWORKCATALOG` disparado por `SendStringToExecute`.
