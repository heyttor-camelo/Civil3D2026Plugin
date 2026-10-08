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
