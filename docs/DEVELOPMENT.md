# Padrao de desenvolvimento do Civil3D2026Plugin

## 1. Regra de namespaces Autodesk

O problema recorrente de `DBObject`, `Entity`, `Erase`, `IsErased` etc. nasce quando um arquivo importa simultaneamente:

```csharp
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
```

e depois usa nomes ambiguos sem qualificacao.

Para **todo codigo novo**, use os aliases globais ja definidos:

```csharp
AcadDb.DBObject
AcadDb.Entity
AcadDb.Solid3d
CivilDb.FeatureLine
CivilDb.Corridor
```

Ou crie aliases especificos no proprio arquivo:

```csharp
using AutoCorridorFeatureLine = Autodesk.Civil.DatabaseServices.AutoCorridorFeatureLine;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
```

**Nao adicione um segundo `IExtensionApplication`.** Existe apenas um: `Infrastructure/PluginApplication.cs`.

## 2. Como adicionar um comando

1. Coloque a classe no diretorio correto em `Modules/<Categoria>/...`.
2. Adicione o nome publico em `Infrastructure/CommandNames.cs`.
3. No `[CommandMethod]`, use a constante, nunca uma string literal.
4. Se for uma nova classe de comandos, registre-a uma vez em `Infrastructure/CommandRegistration.cs`.
5. Adicione o comando/modulo em `Infrastructure/CommandCatalog.cs`.
6. Se o modulo tiver versao propria, adicione/atualize em `Infrastructure/PluginVersions.cs`.
7. Defina `ShowInRibbon: false` somente se o comando for tecnico/interno e nao deva aparecer na aba.
8. Execute `tools/ValidateProject.ps1`.
9. Recompile e faca `NETLOAD` da DLL em `NETLOAD\Civil3D2026Plugin.dll`.

Exemplo:

```csharp
[CommandMethod(C3DCommands.Corridor.SolidArray, CommandFlags.Modal)]
public void CriarArray()
{
    // ...
}
```

## 3. Renomear um comando

Altere a constante correspondente em:

```text
Infrastructure/CommandNames.cs
```

Os `CommandMethod`, a arvore do plugin e a Ribbon consomem essas constantes.

Evite escrever o nome do comando diretamente em novos textos de ajuda; use tambem `C3DCommands...`.

## 4. Banner e arvore no NETLOAD

A inicializacao e centralizada em `PluginApplication`.

O banner usa `CommandCatalog`, portanto modulos/comandos devem ser cadastrados nele.

O banner sempre imprime o caminho real de `Assembly.GetExecutingAssembly().Location`, permitindo conferir qual DLL esta realmente carregada.

## 5. Ribbon / interface grafica

A aba atual se chama:

```text
C3D Tools
```

Ela e gerada em runtime por:

```text
UI/Ribbon/RibbonUiManager.cs
```

e consome diretamente `CommandCatalog`.

Regras:

- nao manter uma segunda lista de comandos dentro da UI;
- `Category`/`ModuleDefinition` definem agrupamento;
- `CommandDefinition.Name` e o comando enviado ao AutoCAD;
- `Title` e o texto do botao;
- `Description` alimenta tooltip e tooltip expandido;
- `ShowInRibbon` controla se o item aparece;
- o primeiro comando visivel de cada modulo e botao grande;
- os demais sao botoes padrao;
- os icones atuais sao gerados por `RibbonIconFactory`.

Comandos de manutencao:

```text
C3DRIBBON
C3DRIBBONRESET
```

Uma futura PaletteSet, janela WPF ou outra GUI deve consumir o mesmo `CommandCatalog`.

## 6. Build

Configuracao oficial atual:

```text
TargetFramework: net8.0-windows
Platform: x64
Civil 3D: 2026.2
UI: WPF habilitado
```

A saida e fixa:

```text
NETLOAD\Civil3D2026Plugin.dll
```

As DLLs Autodesk continuam com `Private=False` e nao sao copiadas para a saida.

A Ribbon usa `AdWindows.dll`, tambem com `Private=False`.

## 7. Regra de namespaces de modulos

Nao use como segmento de namespace interno nomes de tipos centrais do AutoCAD/Civil 3D.

**Errado:**

```csharp
namespace Civil3D2026Plugin.Modules.Corridor;
```

Isso pode sombrear `Autodesk.Civil.DatabaseServices.Corridor` em namespaces irmaos.

**Correto:**

```csharp
namespace Civil3D2026Plugin.Modules.CorridorTools;
```

Para tipos Autodesk sujeitos a colisao, prefira alias explicito:

```csharp
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using AcadEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
```

O script `tools/ValidateProject.ps1` bloqueia segmentos de modulo reservados e avisa quando um arquivo importa simultaneamente os dois `DatabaseServices` de forma ampla.

## 8. Padrao de interface: o painel do Viario

Decisao do David (out/2026): **o painel da rede viaria (`C3DVIARIO`, `Modules/Viario/ViarioPanel.cs` + `ViarioIcons.cs`) e o padrao para todos os plugins/modulos novos** - icones, estruturacao do modulo, monitor (vista grafica), painel e menus. Antes de criar uma tela nova, abrir esses dois arquivos e seguir o mesmo molde; nao inventar outro layout. (Explorador de layouts, MFREBAIXO e Visualizador de secao ja seguem a mesma estrutura de janela.)

### Estrutura do modulo
- Uma janela WPF **nao modal** por modulo (`SW.Window`, instancia unica, `ShowPanel(categoria?)` com `AcApp.ShowModelessWindow`), aberta por um comando de painel (`C3D<MODULO>`) e por atalhos que abrem direto numa categoria.
- Comandos de linha de comando continuam existindo para o que pede clique no desenho; o painel manda o comando (`SendStringToExecute`) em vez de duplicar a logica.
- Dados do modulo no proprio DWG (NOD/XData), configuracoes do usuario no arquivo de configuracao do modulo; nada depende de arquivo externo para funcionar.
- Logica (calculo, leitura do desenho) separada da tela: a tela so le, mostra e chama.

### Layout da janela (de cima para baixo)
1. **Barra de categorias**: botoes com icone e contagem entre parenteses (aviso em destaque quando ha atencao); a categoria escolhida fica marcada.
2. **Barra de comandos**: botoes so com icone (20 px), agrupados por assunto com separadores verticais; tooltip rico (titulo, desenho grande do icone, descricao, passos numerados e o nome do comando). Botoes com opcoes abrem **menu suspenso** (ContextMenu no tema). A direita, "Localizar:" filtra a lista.
3. **Monitor (vista grafica)**: Canvas proprio com fundo escuro (`GraphicBack`), roda = zoom, arrastar = pan, duplo clique = zoom no desenho; barra embaixo com informacao do que esta sob o cursor/coordenadas e o menu **"Textos ▾"** para ligar/desligar cada texto da vista. A vista reage a selecao da lista e vice-versa.
4. **GridSplitter** entre o monitor e a parte de baixo.
5. **Lista** (ListView/GridView): coluna de aviso, icone + primeira coluna, numeros a direita em fonte mono; caixas de marcacao para agir em lote (Shift/Ctrl, espaco); filtro por coluna no cabecalho; botao direito com as acoes do item.
6. **Abas** do item selecionado: "Propriedades" (campos editaveis + botoes de acao) sempre; abas de contexto conforme o tipo do item; aba de recomendacoes/avisos com fonte (normas) quando houver.
7. **Barra de status** com o resumo e o ultimo resultado.

### Icones (estilo do Viario)
- Pictogramas vetoriais desenhados em codigo (`ViarioIcons`: grade **32x32**, partes com traco/preenchimento), sem arquivos externos.
- Usados no tamanho pequeno (18-20 px na barra/categorias/lista), grande no tooltip (96 px) e na Ribbon.
- **Cores com significado**, sempre as mesmas: branco = estrutura/geometria principal; ciano = bordo/linha de referencia; verde = meio-fio/elemento construido; vermelho tracejado = eixo; amarelo = destaque/acao; laranja e magenta para elementos secundarios; vermelho para remover/erro.
- Tracos 1,2 a 1,8 na grade 32, pontas e juncoes arredondadas; nada de letras no lugar do desenho.
- Visual de software de engenharia (como os do AutoCAD), nunca de app de celular: **sem placas arredondadas na Ribbon** (decisao de out/2026; na Ribbon o desenho vai direto no fundo, cores pelo tema do AutoCAD).

### Menus e estilos
- Tema claro/escuro (`ToolTheme`), com estilos proprios para ContextMenu, MenuItem (submenu e marca) e Separator, para ficarem legiveis nos dois temas (ver `AddStyles` do ViarioPanel e `UI/Common/ToolStyles.cs`).
- Textos curtos nos botoes, explicacao no tooltip; termos do Civil em ingles quando o usuario usa assim (alignment, profile view, band set...); "elevacao" para altura de ponto, "cota" so para dimensao.
- Operacoes longas com a janela de progresso comum (`Progresso`/`ProgressoJanela`) e tudo registrado no log comum (`PluginLog`, Ctrl+Shift+L).
