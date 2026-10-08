# CORRSPLIT v1.0 - Corridor Split

Comando: CORRSPLIT
DLL: Civil3D2026Plugin.dll (Civil 3D 2026.2 / .NET 8 x64)

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
- Replica estacas inicial/final, frequencias de assemblies e estacas adicionais com descricoes.
- Remapeia targets pelo contexto da subassembly no destino: grupo, nome logico, tipo e nome exibido.
- Replica Corridor Transition Sets inteiros e contidos nas regioes selecionadas, com valores inicial/final, parametros, tipo, comentarios e lado.
- Verifica quantidade de regioes, Assembly, limites, parte das frequencias, targets e quantidade de conjuntos de transitions; reconstrui o novo Corridor antes de alterar a origem.
- Operacao ocorre dentro de uma mesma transacao do desenho; em excecao, a transacao nao e confirmada.

## Limitacoes deliberadas

- NAO clona Corridor Surfaces, boundaries, slope patterns, code set styles, overrides ou objetos derivados. O operador e avisado se a origem tem superficies.
- Nao permite regioes com overrides de secoes ou Offset Baselines.
- Nao permite Corridor Transitions cruzando a fronteira selecionada ou Transition Set misto (transicoes dos dois grupos). Separe os conjuntos primeiro.
- Nao compara a geometria tridimensional completa das secoes.
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
