# MFSARJETA (Subassembly Composer)

Meio-fio SUDECAP 30/40, Kassel (guia de atracagem) e personalizado, com sarjeta
opcional e berço (base, sub-base e reforço recortados pelo meio-fio).

- `MFSARJETA_v1.12.pkt`: pacote para importar no Civil 3D.
- `gen_mfsarjeta.py`: gerador do pacote (o XAML é escrito pelo script, não à mão).
  Uso: `python gen_mfsarjeta.py MFSARJETA_vX.pkt`

Regra de versão: nunca mudar o tipo de dado de um parâmetro existente
(o "change subassembly .pkt" do Civil 3D não converte e deixa o campo vazio);
se precisar mudar, usar outro nome.

## Code do rebaixo (hachura em planta)

Parâmetro de texto `CODE_REB` ("Code do rebaixo (texto)"). O texto digitado vira
um code a mais no topo do meio-fio e no topo da sarjeta (os dois com o mesmo code).
Vazio = sem code extra. Ex.: digite `REBAIXO` nas seções do rebaixo (Section Editor
ou propriedades da subassembly) e dê a esse link code um Material Area Fill Style.

O parâmetro numérico `DESTAQUE` (v1.10/1.11) saiu; o MFREBAIXO não grava mais
transição de destaque nesse PKT.

## Kassel

Dimensões fixas (H 40, pé 15 rente ao pavimento, exposto 25). Ignora o alvo de
topo, a altura livre e o "Esticar".
