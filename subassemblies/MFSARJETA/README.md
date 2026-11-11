# MFSARJETA (Subassembly Composer)

Meio-fio SUDECAP 30/40, Kassel (guia de atracagem) e personalizado, com sarjeta
opcional e berço (base, sub-base e reforço recortados pelo meio-fio).

- `MFSARJETA_v1.25.pkt`: pacote para importar no Civil 3D.
- `gen_mfsarjeta.py`: gerador do pacote (o XAML é escrito pelo script, não à mão).
  Uso: `python gen_mfsarjeta.py MFSARJETA_vX.pkt` (no Windows sem `python` no PATH: `py gen_mfsarjeta.py ...`)

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
`DESTAQUE` continua existindo (o comando ainda grava a Transition dele), mas desde a v1.24 não
dá mais code nenhum.

## Sólidos do corredor (v1.24)

Os codes dos **shapes** são fixos ao longo do corredor (sarjeta: `Sarjeta`, `SARJETA`; meio-fio:
`Curb` + tipo; rampa da calçada: `Calcada`, `CALCADA`). Até a v1.23 o shape do meio-fio levava
`MF_NORMAL`/`MF_REBAIXADO` e `REBAIXO`; como a extração de sólidos só liga shapes com os mesmos
codes entre duas seções, o sólido ficava com um buraco em cada rampa do rebaixo. O estado
`MF_NORMAL`/`MF_REBAIXADO` continua no ponto `Back_Curb`.

## Calçada

`Calçada` = Yes. Começa no topo de trás do MF. A borda externa fica na cota normal
(topo normal do MF + declividade x largura) ou nos alvos de offset/cota. No rebaixo
(`REB` > 0) a rampa sai do topo rebaixado com `iRampa` (8,33%) até encontrar a calçada
normal. Se não couber na largura, a calçada inteira fica em `iRampa` (sem alvo) e
`Rampa OK` = 0. `Faixa livre` = largura que sobra depois da rampa (NBR 9050: >= 1,20 m).

## Meio-fio levantado (v1.25)

`REB` negativo sobe o meio-fio (o MFREBAIXO grava assim quando a altura final pedida é maior que a
normal). A peça estica assim que o enterrado ficaria menor que o `Enterrado mínimo` (`EMB`); se a seção
normal já tinha enterrado menor que `EMB`, mantém esse enterrado (o fundo da peça não sobe). Assim a
altura da peça muda sem salto ao longo das rampas (ex. T30 com 12 cm enterrados levantado para 35 cm:
`H_REAL` = 47 cm). Com calçada,
a rampa desce do topo levantado até a calçada normal com `iRampa`; se não couber, a calçada desce reta
até a borda externa e `Rampa OK` = 0.

## Kassel

Dimensões fixas (H 40, pé 15 rente ao pavimento, exposto 25). Ignora o alvo de
topo, a altura livre e o "Esticar". Desde a v1.23 aceita a sarjeta (`Sarjeta` = Yes), com a mesma
largura `LS`/alvo de offset dos outros tipos: na troca T30/T40 -> Kassel as faces da sarjeta
ficam alinhadas. O pé do Kassel encosta na sarjeta na flowline.
