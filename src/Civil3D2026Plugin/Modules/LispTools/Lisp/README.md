# LISPs embutidas na DLL

Todo `.lsp`, `.vlx` ou `.fas` desta pasta vai dentro da `Civil3D2026Plugin.dll`.
Depois de recompilar, a rotina aparece no `C3DLISP` e o comando fica disponivel em todo desenho.

## Cabecalho

Coloque no topo do arquivo (linhas de comentario, sem acento para servir a arquivos ANSI e UTF-8):

```lisp
;;@TITULO    Nome amigavel da rotina
;;@CATEGORIA Hachuras
;;@ICONE     hachura          (opcional; sem ele vale o icone da categoria)
;;@COMANDO   NOMECMD          (uma linha por comando exibido)
;;@VERSAO    1.0
;;@DESC      O que a rotina faz (varias linhas sao juntadas)
;;@USO       Passo de uso (cada linha vira um item numerado)
```

Todas as tags sao opcionais. Sem `;;@COMANDO`, a lista mostra os `(defun c:XXX)` encontrados no arquivo.
O carregamento usa sempre todos os `(defun c:XXX)` do arquivo, mesmo os que nao aparecem na lista.

Icones: `hachura`, `zebra`, `diagnostico`, `polilinha`, `offset`, `curva`, `via`, `prancha`, `imprimir`,
`camadas`, `malha`, `mapa`, `bloco`, `numero`, `cor`, `medida`, `tabela`, `solido3d`, `juntar`, `quebrar`
(desenhados em `Modules/LispTools/ToolIcons.cs`).

Categorias em uso: `3D`, `Blocos, textos e cores`, `Hachuras`, `Polilinhas e geometria`,
`Pranchas e layouts`, `Tabelas e Excel`, `Vias e meio-fio`.

## Arquivos compilados (VLX/FAS)

Nao da para ler o cabecalho de dentro deles. Crie um arquivo ao lado com o mesmo nome mais `.info`
(ex.: `MALHA.VLX.info`) contendo as mesmas linhas `;;@`. Esses arquivos sao carregados inteiros ao abrir o desenho.

## Quando converter para C#

Rotinas curtas e estaveis passam para `Modules/Utilitarios` (mesmo nome de comando) e o `.lsp` sai daqui.
Ja convertidas: ALLTORGB, TLEN, JOBECOUNT, SMARTHATCH, LSLAYOUTS, MOFF/MOFFSET, SEPARA_CURVAS_RAIO, KB/PP, NP (modulo Layouts).
Os originais ficam no historico do git.

## Carregamento

- `.lsp` com comandos: registrados por `autoload`; o arquivo so e carregado no primeiro uso do comando.
- `.lsp` sem `(defun c:...)`, `.vlx` e `.fas`: carregados inteiros em cada desenho.
- Cuidado com rotinas que redefinem funcoes globais com o mesmo nome de outra rotina desta pasta:
  a ultima carregada vence. Mantenha so uma versao de cada rotina (por isso o `VIAU.lsp` v0.1 nao esta aqui).
