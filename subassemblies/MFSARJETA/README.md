# MFSARJETA (Subassembly Composer)

Meio-fio SUDECAP 30/40, Kassel (guia de atracagem) e personalizado, com sarjeta
opcional e berço (base, sub-base e reforço recortados pelo meio-fio).

- `MFSARJETA_v1.11.pkt`: pacote para importar no Civil 3D.
- `gen_mfsarjeta.py`: gerador do pacote (o XAML é escrito pelo script, não à mão).
  Uso: `python gen_mfsarjeta.py MFSARJETA_vX.pkt`

Regra de versão: nunca mudar o tipo de dado de um parâmetro existente
(o "change subassembly .pkt" do Civil 3D não converte e deixa o campo vazio);
se precisar mudar, usar outro nome.

## DESTAQUE (hachura de rebaixo em planta)

O `MFREBAIXO` grava no `DESTAQUE` um Corridor Transition 1->2 (aba de entrada),
2->3 (rebaixo pleno) e 3->4 (aba de saída). Os links do topo sempre levam um
destes codes (para hachurar, dê a eles um Material Area Fill Style no Code Set Style):

| Link              | Normal | Aba (rampa) | Rebaixo pleno |
|-------------------|--------|-------------|---------------|
| Topo do meio-fio  | `MF_N` | `MF_ABA`    | `MF_REB`      |
| Topo da sarjeta   | `SJ_N` | `SJ_ABA`    | `SJ_REB`      |

## Kassel

Dimensões fixas (H 40, pé 15 rente ao pavimento, exposto 25). Ignora o alvo de
topo, a altura livre e o "Esticar".
