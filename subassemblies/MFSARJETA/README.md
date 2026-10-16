# MFSARJETA (Subassembly Composer)

Meio-fio SUDECAP 30/40, Kassel (guia de atracagem) e personalizado, com sarjeta
opcional e berço (base, sub-base e reforço recortados pelo meio-fio).

- `MFSARJETA_v1.10.pkt`: pacote para importar no Civil 3D.
- `gen_mfsarjeta.py`: gerador do pacote (o XAML é escrito pelo script, não à mão).
  Uso: `python gen_mfsarjeta.py MFSARJETA_vX.pkt`

Regra de versão: nunca mudar o tipo de dado de um parâmetro existente
(o "change subassembly .pkt" do Civil 3D não converte e deixa o campo vazio);
se precisar mudar, usar outro nome.

## DESTAQUE (hachura de rebaixo em planta)

O `MFREBAIXO` grava no `DESTAQUE` um Corridor Transition 1->2 (aba de entrada),
2->3 (rebaixo pleno) e 3->4 (aba de saída). O pkt troca o code extra dos links
do topo: `MF_ABA`/`MF_REB` no meio-fio e `SJ_ABA`/`SJ_REB` na sarjeta.
No Code Set Style, dê a esses link codes um Material Area Fill Style.
