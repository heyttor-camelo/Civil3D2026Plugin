# Icones do plugin

Fonte unica dos icones dos paineis e da Ribbon (grade 16x16).

- `icones.py`: o desenho de cada icone, em camadas (L traco no tom do texto, A traco na cor de destaque, F massa na cor de destaque, S marca ambar, W massa no tom do texto). Caminhos no formato SVG/WPF.
- `ribbon.py`: comando da Ribbon -> icone e selo da variante; selos; cores da placa por categoria.
- `previa.py`: gera `painel_escuro.png`, `painel_claro.png` e `ribbon.png` para conferir (`pip install cairosvg`; `python previa.py <pasta de saida>`).
- `gerar.py`: escreve `src/Civil3D2026Plugin/UI/Common/IconArt.cs` a partir de `icones.py` e `ribbon.py`.

Fluxo: editar `icones.py`/`ribbon.py` -> `python previa.py .` para olhar -> `python gerar.py` -> compilar.
Comando novo sem entrada em `ribbon.py` cai num icone pelo prefixo do nome (RibbonIconFactory.PorPrefixo).
