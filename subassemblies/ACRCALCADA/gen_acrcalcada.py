#!/usr/bin/env python3
"""Gera o pacote .pkt ACRCALCADA: acrescimo de calcada + canaleta com grelha (alteamento de
meio-fio, Proposta 01). Encaixa no ponto de tras (topo) de qualquer meio-fio e cresce para o
lado da calcada existente.

Geometria (a partir de P1 = topo de tras do MF novo):
    P1 ----- acrescimo (i_ACR, desce) ----- P2 |par| grelha |par| P5   <- topo da caixa (alvo de cota)
    |                                       |   P3 -------- P4   |
    P12 ---- fundo (E_ACR) --------------- P13  |  canaleta    |   |
                                            |   P6 -------- P7   |   <- fundo interno (alvo de cota)
                                            P8 ---- laje ------ P9
                                                                 ^ face externa da caixa (alvo de offset)
"""
import sys
import os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pktlib
from pktlib import var, pt, pt_slope, lk, sh, out, Switch

OUT = sys.argv[1]
NAME = "ACRCALCADA"
VERSION = "1.0"

# (nome, tipo, default, display)  tipo: double|grade|enumYN|enum:<Grupo>
# Regra de versao: nunca mudar o TIPO de dado de um parametro existente.
ENUM_GROUPS = {}
PARAMS = [
    ("L_ACR",    "double", "1.51",   "Acréscimo: largura"),
    ("i_ACR",    "grade",  "-0.027", "Acréscimo: declividade (- desce p/ canaleta)"),
    ("E_ACR",    "double", "0.20",   "Acréscimo: espessura"),
    ("iMIN",     "grade",  "0.01",   "Declividade mínima"),
    ("iMAX",     "grade",  "0.03",   "Declividade máxima"),
    ("L_CX",     "double", "0.40",   "Canaleta: largura interna"),
    ("H_CX",     "double", "0.21",   "Canaleta: profundidade interna"),
    ("E_PAR",    "double", "0.05",   "Canaleta: parede"),
    ("E_FUNDO",  "double", "0.10",   "Canaleta: laje de fundo"),
    ("E_GRELHA", "double", "0.05",   "Grelha: espessura"),
]
TARGETS = [  # (nome, display, preview, ativo no preview, tipo)
    ("CX_OFFSET",     "Caixa: face externa (offset)", "2.0",  False, "offset"),
    ("CX_TOPO_ELEV",  "Caixa: topo (cota)",           "-0.04", False, "elev"),
    ("CX_FUNDO_ELEV", "Caixa: fundo interno (cota)",  "-0.25", False, "elev"),
]
OUTPUTS = [
    ("I_REAL",     "Declividade real do acréscimo (+ desce)"),
    ("I_OK",       "Declividade OK (1 = entre mín. e máx.)"),
    ("L_ACR_REAL", "Largura real do acréscimo"),
    ("H_CX_REAL",  "Profundidade real da canaleta"),
    ("DESNIVEL",   "Desnível MF -> topo da caixa"),
]

EXPR = {
    # Grade -> Double (IF misturando Grade e Double nao compila)
    "IA":   "[i_ACR + 0.0]",
    "IMN":  "[iMIN + 0.0]",
    "IMX":  "[iMAX + 0.0]",
    "LCXT": "[L_CX + 2*E_PAR]",
    # largura total ate a face externa da caixa: alvo de offset ou L_ACR + caixa
    "WT":   "[IF(CX_OFFSET.IsValid, Math.Abs(CX_OFFSET.Offset - P1.Offset), L_ACR + LCXT)]",
    "WA":   "[Math.Max(0.05, WT - LCXT)]",
    # fundo interno: alvo de cota ou H_CX abaixo do topo da caixa
    "ZF":   "[IF(CX_FUNDO_ELEV.IsValid, CX_FUNDO_ELEV.Elevation, P2.Elevation - H_CX)]",
    "DCX":  "[Math.Max(0.05, P2.Elevation - ZF)]",
    "IR":   "[(P1.Elevation - P2.Elevation)/WA]",
    "IOK":  "[IF(IR >= IMN - 0.00005 AndAlso IR <= IMX + 0.00005, 1, 0)]",
    # codes de aviso: a hachura/marcacao muda quando a declividade sai da faixa
    "AcrCode": '[IF(IOK > 0.5, "ACRESCIMO", "ACRESCIMO_FORA")]',
}
VARTYPES = {k: ("String" if k.endswith("Code") else "Double") for k in EXPR}
pktlib.EXPR.update(EXPR); pktlib.VARTYPES.update(VARTYPES)

TOP = ["Top", "TOPO"]
for v in ("IA", "IMN", "IMX", "LCXT", "WT", "WA"): var(v)
pt("P1", None, "0", "0", codes=["Acr_Ini"], disp="P1 - topo de trás do MF (origem)")
pt_slope("P2", "P1", "[IA]", "[WA]", "[CX_TOPO_ELEV]", codes=["Acr_Fim"],
         disp="P2 - topo da caixa, face do acréscimo (alvo cota)")
for v in ("ZF", "DCX", "IR", "IOK", "AcrCode"): var(v)

# acrescimo de calcada (espessura constante)
lk("L1", "P1", "P2", codes=["Calcada"] + TOP, disp="L1 - topo do acréscimo")
pt("P13", "P2", "0", "[-E_ACR]", link="L2", lcodes=["Calcada"], disp="P13&L2 - fundo do acréscimo na caixa")
pt("P12", "P1", "0", "[-E_ACR]", disp="P12 - fundo do acréscimo no MF")
lk("L3", "P13", "P12", codes=["Calcada_Fundo", "Datum"], disp="L3 - fundo do acréscimo")
lk("L4", "P12", "P1", codes=["Calcada"], disp="L4 - encosto no MF")
sh("S1", ["L1", "L2", "L3", "L4"], codes=["Calcada", "CALCADA", "[AcrCode]"], disp="S1 - ACRÉSCIMO DE CALÇADA")

# caixa (canaleta em U): paredes E_PAR, laje E_FUNDO
pt("P3", "P2", "[E_PAR]", "0", link="L5", lcodes=["Canaleta"] + TOP, disp="P3&L5 - topo da parede (lado MF)")
pt("P4", "P3", "[L_CX]", "0", codes=["Grelha_Ext"], disp="P4 - topo interno (lado calçada existente)")
pt("P5", "P4", "[E_PAR]", "0", codes=["Acr_Ext"], link="L9", lcodes=["Canaleta"] + TOP,
   disp="P5&L9 - topo da parede externa (fim)")
pt("P6", "P3", "0", "[-DCX]", link="L6", lcodes=["Canaleta"], disp="P6&L6 - fundo interno (lado MF)")
pt("P7", "P4", "0", "[-DCX]", link="L8", lcodes=["Canaleta"], disp="P7&L8 - fundo interno (lado externo)")
lk("L7", "P6", "P7", codes=["Canaleta", "Canaleta_Fundo"], disp="L7 - fundo interno da canaleta")
pt("P9", "P5", "0", "[-(DCX + E_FUNDO)]", link="L10", lcodes=["Canaleta"], disp="P9&L10 - fundo da laje (externo)")
pt("P8", "P2", "0", "[-(DCX + E_FUNDO)]", link="L12", lcodes=["Canaleta"], disp="P8&L12 - fundo da laje (lado MF)")
lk("L11", "P9", "P8", codes=["Canaleta", "Datum"], disp="L11 - fundo da laje")
sh("S2", ["L5", "L6", "L7", "L8", "L9", "L10", "L11", "L12"], codes=["Canaleta", "CANALETA"], disp="S2 - CANALETA (caixa em U)")

# grelha / placa drenante entre as paredes, topo no nivel da caixa
lk("L13", "P3", "P4", codes=["Grelha"] + TOP, disp="L13 - topo da grelha")
pt("P11", "P4", "0", "[-E_GRELHA]", link="L14", lcodes=["Grelha"], disp="P11&L14 - fundo da grelha (externo)")
pt("P10", "P3", "0", "[-E_GRELHA]", link="L16", lcodes=["Grelha"], disp="P10&L16 - fundo da grelha (lado MF)")
lk("L15", "P11", "P10", codes=["Grelha"], disp="L15 - fundo da grelha")
sh("S3", ["L13", "L14", "L15", "L16"], codes=["Grelha", "GRELHA"], disp="S3 - GRELHA")

# saidas
out("I_REAL", "[IR]")
out("I_OK", "[IOK]")
out("L_ACR_REAL", "[WA]")
out("H_CX_REAL", "[DCX]")
out("DESNIVEL", "[P1.Elevation - P2.Elevation]")

pktlib.write(OUT, NAME, VERSION, PARAMS, TARGETS, OUTPUTS, ENUM_GROUPS)
