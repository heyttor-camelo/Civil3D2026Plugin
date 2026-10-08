# Ribbon / UI

A aba **C3D Tools** e gerada em runtime a partir de `Infrastructure/CommandCatalog.cs`.

## Principios

- `CommandNames.cs` continua sendo a fonte unica dos nomes publicos.
- `CommandCatalog.cs` contem titulo, descricao, modulo e visibilidade na Ribbon.
- A Ribbon nao chama metodos de negocio diretamente; ela envia o mesmo comando do AutoCAD/Civil 3D.
- Cada modulo visivel no catalogo vira um painel.
- O primeiro comando de cada modulo aparece como botao grande; os demais como botoes padrao.
- Os tooltips usam `Title`, `Content` e `ExpandedContent`, exibindo descricao, comando, grupo, modulo e versao.
- Os icones atuais sao gerados em memoria por `RibbonIconFactory`; podem ser trocados por recursos PNG/SVG no futuro sem mudar a arquitetura.

## Comandos de manutencao da UI

- `C3DRIBBON`: cria/ativa a aba.
- `C3DRIBBONRESET`: remove e recria a aba a partir do catalogo atual.
- `C3DHELP`: imprime a arvore completa de comandos.

## Futuras interfaces

Uma palette, janela WPF ou outra interface deve consumir o mesmo `CommandCatalog`, evitando listas paralelas de comandos.
