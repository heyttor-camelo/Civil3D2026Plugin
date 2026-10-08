# CORRSPLIT v1.5 - Corridor Split

Comando: CORRSPLIT
DLL: Civil3D2026Plugin.dll (Civil 3D 2026.2 / .NET 8 x64)

## Atualizacao v1.5 — estacas adicionais apos reconstruir o destino (08/10/2026)

O erro apos transferir targets indicou estacas adicionais divergentes em
TRECHO - _CURB - 210. A validacao anterior nao distinguia diferencias na frequencia
de montagem, ordem da lista de estacas ou estacas realmente ausentes.

- Configuracao de frequencias separada da copia de estacas adicionais.
- Apos o primeiro rebuild do corredor novo, a rotina recria estacas adicionais
  com BaselineRegion.ClearAdditionalStations e BaselineRegion.AddStation(estaca, descricao).
- Apos aplicar os targets e reconstruir, confere as estacas sem depender
  da ordem retornada pela API, tolerancia numerica 1e-6, preservando descricao.
- Se houver diferenca, tenta uma unica restauracao e rebuild adicionais.
- Se persistir, cancela a transacao e mostra contagem, valores e descricoes das
  estacas de origem e destino, para nao perder silenciosamente estaqueamento.
- Ainda nao foi compilado/testado com as DLLs Autodesk; use somente Copiar
  no primeiro teste em uma copia do DWG.

## Atualizacao v1.4 — validacao por tipo de target (08/10/2026)

O stack trace do parametro PAV_ESQ / Outside Elevation, tipo Elevation, 2 TargetIds,
revelou que UseSameSideTarget rejeita targets diferentes de Offset,
mesmo quando existem dois ou mais. OffsetPipe tambem e rejeitado.

Centralizada a regra CanReadSameSideTarget: so permite GET/SET quando
TargetType == SubassemblyLogicalNameType.Offset em ambos os lados e
TargetIds.Count >= 2 em cada lado. Todas as consultas em CopyTargets,
validacao imediata e VerifyTargets apos rebuild usam essa mesma regra.

O mapeamento de IDs para Elevation, OffsetPipe e outros tipos continua
intacto, pois a limitacao se refere exclusivamente a UseSameSideTarget.

## Atualizacao v1.3 — getter protegido (08/10/2026)

O stack trace identificou que a propriedade UseSameSideTarget lanca excecao ate no GET
quando a subassembly tem apenas um TargetId (por exemplo, PAV_DIR / Lane Width).
Corrigidas todas as leituras e gravacoes dessa propriedade (CopyTargets e verificacoes)
para executarem exclusivamente quando as DUAS colecoes, origem e destino, possuem
pelo menos dois IDs. A comparacao de TargetToOption segue a mesma protecao.

Teste primeiro com o modo Copiar e, em caso de erro, envie o novo stack trace.

## Atualizacao v1.2 (diagnostico do erro TargetIds)

- Corrige a ordem da construcao: cria todas as baselines e regioes, faz primeiro Rebuild, aplica targets e faz Rebuild final.
- Faz a contagem de TargetIds separadamente em cada parametro, nao entre parametros horizontais e verticais distintos.
- Nao reatribui TargetIds com zero objetos, nem TargetToOption quando existem menos de dois IDs no mesmo parametro.
- SetTargets e ignorado quando nenhum parametro sofreu alteracao.
- Falhas de leitura, atribuicao e verificacao informam regiao, subassembly, parametro, contagem de IDs, etapa e stack trace.
- A funcionalidade de selecionar uma regiao por duplo clique diretamente no desenho e o highlight visual continuam pendentes.
- Importante: reiniciar o Civil 3D apos recompilar, pois NETLOAD nao substitui necessariamente uma DLL ja carregada.

## Novidades da v1.1

- Janela de baselines e regioes hierarquicas como Corridor Properties: colunas Horizontal Baseline, Vertical Baseline, Assembly, estacas, targets e checkboxes.
- Selecao individual, por baseline, marcar todas, inverter e expandir/recolher.
- Selecao de uma regiao pelo desenho com StartUserInteraction do editor e proximidade ao eixo da baseline (tolerancia 35 unidades). Selecoes ambiguas exigem escolha manual para nao marcar a regiao errada.
- Corrige erro: The count of TargetIds should be greater or equal to 2. A opcao TargetToOption e redefinida apenas quando ha 2+ targets; casos com 0/1 sao transferidos sem impor opcao sem efeito.
- Iconografia da Ribbon inspirada nas ferramentas CAD da imagem de referencia.

## Como usar

1. Salve uma copia do DWG.
2. Execute NETLOAD com NETLOAD/Civil3D2026Plugin.dll.
3. Digite CORRSPLIT e selecione o corredor original.
4. Marque as regioes que devem ir ao novo corredor (pode selecionar regioes de baselines diferentes).
5. Informe o novo nome. Escolha Copiar (mantem origem) ou Transferir (remove da origem).
6. Confira as mensagens e o modelo no Prospector; para testes, use primeiro Copiar.

## Alcance da primeira versao

- Cria apenas baselines com regioes selecionadas.
- Suporta baselines baseadas em Alignment + Profile e em Feature Line.
- Reutiliza Assembly e referencias de target ja existentes no mesmo DWG.
- Copia Code Set Style, modo de travamento das regioes e tamanho maximo do lado de triangulo da superficie.
- Replica estacas inicial/final, frequencias de assemblies e estacas adicionais com descricoes.
- Remapeia targets pelo contexto da subassembly no destino: grupo, nome logico, tipo e nome exibido.
- Replica Corridor Transition Sets inteiros e contidos nas regioes selecionadas, com valores inicial/final, parametros, tipo, comentarios e lado.
- Verifica quantidade de regioes, Assembly, limites, parte das frequencias, targets e quantidade de conjuntos de transitions; reconstrui o novo Corridor antes de alterar a origem.
- Operacao ocorre dentro de uma mesma transacao do desenho; em excecao, a transacao nao e confirmada.

## Limitacoes deliberadas

- NAO clona Corridor Surfaces, boundaries, slope patterns, overrides ou objetos derivados. O operador e avisado se a origem tem superficies.
- Nao permite regioes com overrides de secoes ou Offset Baselines.
- Nao permite Corridor Transitions cruzando a fronteira selecionada ou Transition Set misto (transicoes dos dois grupos). Separe os conjuntos primeiro.
- Nao compara a geometria tridimensional completa das secoes.
- A selecao pelo desenho e por distancia aproximada do ponto clicado ate o eixo da baseline; quando houver ambiguidades, selecione pela arvore.
- Targets dependentes de entidades dinamicas podem exigir verificacao manual apos transferencia.
- Nao exclui baselines vazias no corredor original.
- Nao promete equivalencia total de corredores complexos nesta primeira versao.

## Teste recomendado no Civil 3D

Em uma copia do DWG:
1. Corridor simples com 2 baselines (Alignment e Feature Line), 2 regioes em cada.
2. Selecione uma regiao de cada baseline em Copiar; confira 2 baselines no destino.
3. Confira Targets e Frequencies nas propriedades das regioes, e transitions no editor.
4. Repita em Transferir e confira originais, destino e UNDO/rollback.
5. Teste transicao cruzando fronteira e overrides: deve cancelar sem alterar o modelo.

Status: codigo integrado ao repositorio. Exige build e teste funcional no Civil 3D 2026.2; as DLLs Autodesk nao estao disponiveis no ambiente de revisao.
