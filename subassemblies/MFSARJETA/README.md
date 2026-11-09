# MFSARJETA (Subassembly Composer)

Meio-fio SUDECAP 30/40, Kassel (guia de atracagem) e personalizado, com sarjeta
opcional e berço (base, sub-base e reforço recortados pelo meio-fio).

- `MFSARJETA_v1.23.pkt`: pacote para importar no Civil 3D.
- `gen_mfsarjeta.py`: gerador do pacote (o XAML é escrito pelo script, não à mão).
  Uso: `python gen_mfsarjeta.py MFSARJETA_vX.pkt`

Regra de versão: nunca mudar o tipo de dado de um parâmetro existente
(o "change subassembly .pkt" do Civil 3D não converte e deixa o campo vazio);
se precisar mudar, usar outro nome.

## Alvos

- `Flowline` (cota): elevação da flowline (P2).
- `Flowline (offset)` (v1.22): largura da sarjeta = distância horizontal da borda da pista até o alvo.
  Sem alvo vale `LS`. Sem sarjeta o alvo é ignorado. Saída `Largura real da sarjeta` (`LS_REAL`).
- `Topo do meio-fio` (cota), `Calçada: borda externa` (offset e cota).

## Hachura do rebaixo em planta

Desde o MFREBAIXO 1.8 (plugin 2.92) a hachura é **desenhada pelo plugin** (HATCH na layer
`MF-REBAIXO`, da borda da pista à borda da calçada ou ao fundo do MF, em S0..S3). O PKT v1.23
não tem mais os links de sobreposição nem precisa de Code Set Style para isso. O parâmetro
`DESTAQUE` continua existindo (o comando ainda grava a Transition dele) e dá o code `REBAIXO`
só nos shapes da sarjeta, do MF e da rampa da calçada, para quem quiser usá-lo no estilo.

## Calçada

`Calçada` = Yes. Começa no topo de trás do MF. A borda externa fica na cota normal
(topo normal do MF + declividade x largura) ou nos alvos de offset/cota. No rebaixo
(`REB` > 0) a rampa sai do topo rebaixado com `iRampa` (8,33%) até encontrar a calçada
normal. Se não couber na largura, a calçada inteira fica em `iRampa` (sem alvo) e
`Rampa OK` = 0. `Faixa livre` = largura que sobra depois da rampa (NBR 9050: >= 1,20 m).

## Kassel

Dimensões fixas (H 40, pé 15 rente ao pavimento, exposto 25). Ignora o alvo de
topo, a altura livre e o "Esticar". Desde a v1.23 aceita a sarjeta (`Sarjeta` = Yes), com a mesma
largura `LS`/alvo de offset dos outros tipos: na troca T30/T40 -> Kassel as faces da sarjeta
ficam alinhadas. O pé do Kassel encosta na sarjeta na flowline.
