# Testes do C3DVIARIO no Civil 3D

1. Abra um desenho novo, carregue a DLL (NETLOAD).
2. `SCRIPT` e escolha `teste_rede.scr` (com FILEDIA=0, digite o caminho).
3. O script monta uma rede (cruzamento, T, eixo curvo, rotatoria com ramos), gera os
   alinhamentos do Civil e roda `C3DVIARIOTESTE` depois de cada etapa
   (alargamento, gota, raio, mover eixo, faixa livre).
4. Relatorio: `%TEMP%\C3DVIARIOTESTE.txt` (a etapa INICIO zera o arquivo).
   Cada objeto aparece como OK / FALHA / AVISO.
