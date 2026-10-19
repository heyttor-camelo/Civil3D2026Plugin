# ACRCALCADA - acréscimo de calçada + canaleta com grelha

Subassembly para o alteamento de meio-fio (Proposta 01 do ALTEAMENTO_MF_E_SARJETA): encaixa no
ponto de trás (topo) de qualquer meio-fio e cresce para a calçada existente.

```
P1 ---- acréscimo (desce, i_ACR) ---- P2 |par| grelha |par| P5     <- topo da caixa  (alvo CX_TOPO_ELEV)
|                                      |  P3 -------- P4  |
P12 --- fundo (E_ACR) --------------- P13 |  canaleta    |  |
                                       |  P6 -------- P7  |         <- fundo interno (alvo CX_FUNDO_ELEV)
                                       P8 ---- laje ----- P9
                                                           ^ face externa (alvo CX_OFFSET)
```

- Sem alvos: largura L_ACR, declividade i_ACR, profundidade H_CX.
- Com CX_OFFSET: o acréscimo fecha até (alvo - largura da caixa).
- Com CX_TOPO_ELEV: a declividade do acréscimo sai do desnível; I_OK avisa se saiu de iMIN..iMAX
  e o shape recebe o code ACRESCIMO_FORA.
- Shapes/codes: Calcada+CALCADA (acréscimo), Canaleta+CANALETA (caixa em U), Grelha+GRELHA.
- Gerar: `python3 gen_acrcalcada.py ACRCALCADA_v1.0.pkt` (usa pktlib.py do mesmo diretório).
