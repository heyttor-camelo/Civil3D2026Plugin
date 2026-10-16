# MFSARJETA (Subassembly Composer)

Meio-fio SUDECAP 30/40, Kassel (guia de atracagem) e personalizado, com sarjeta
opcional e berço (base, sub-base e reforço recortados pelo meio-fio).

- `MFSARJETA_v1.18.pkt`: pacote para importar no Civil 3D.
- `gen_mfsarjeta.py`: gerador do pacote (o XAML é escrito pelo script, não à mão).
  Uso: `python gen_mfsarjeta.py MFSARJETA_vX.pkt`

Regra de versão: nunca mudar o tipo de dado de um parâmetro existente
(o "change subassembly .pkt" do Civil 3D não converte e deixa o campo vazio);
se precisar mudar, usar outro nome.

## Code REBAIXO (hachura em planta)

O `MFREBAIXO` aplica a geometria (`REB`, profundidade) e o `DESTAQUE` nas mesmas
estacas S0..S3. Com `DESTAQUE` em [1,4) o PKT põe o code `REBAIXO`:
- em links de sobreposição, criados por último, por cima do topo da sarjeta, do topo do MF
  e da rampa da calçada (esses links existem sempre; fora do rebaixo ficam sem code);
- nos shapes da sarjeta, do MF e da rampa da calçada.
Fora do rebaixo (0) e em S3 (4) não há code: a hachura vai exatamente de S0 a S3.
No Code Set Style, dê ao code `REBAIXO` um Material Area Fill Style.

## Calçada

`Calçada` = Yes. Começa no topo de trás do MF. A borda externa fica na cota normal
(topo normal do MF + declividade x largura) ou nos alvos de offset/cota. No rebaixo
(`REB` > 0) a rampa sai do topo rebaixado com `iRampa` (8,33%) até encontrar a calçada
normal. Se não couber na largura, a calçada inteira fica em `iRampa` (sem alvo) e
`Rampa OK` = 0. `Faixa livre` = largura que sobra depois da rampa (NBR 9050: >= 1,20 m).

## Kassel

Dimensões fixas (H 40, pé 15 rente ao pavimento, exposto 25). Ignora o alvo de
topo, a altura livre e o "Esticar".
