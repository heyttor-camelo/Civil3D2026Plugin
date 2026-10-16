#!/usr/bin/env python3
"""Gera o pacote .pkt MFSARJETA (meio-fio SUDECAP 30/40, Kassel, personalizados + sarjeta + berço) para o Subassembly Composer."""
import sys, uuid, zipfile, io
from xml.sax.saxutils import escape

OUT = sys.argv[1]
NAME = "MFSARJETA"
VERSION = "1.16"
GUID = uuid.uuid4().hex

def q(v):  # escape para atributo
    return escape(v, {'"': '&quot;'})

# ----------------------------------------------------------------- parametros
# (nome, tipo, default, display)   tipo: double|grade|enumYN|enum:<Grupo>
# Regra de versão: nunca mudar o TIPO de dado de um parâmetro existente (o "change .pkt" do Civil 3D
# não converte e deixa o campo vazio). Se precisar mudar, usar outro nome. TIPO (double) -> TIPO_MF (lista) na v1.8.
ENUM_GROUPS = {
    "TipoMF": [("T30", "SUDECAP 30", 501), ("T40", "SUDECAP 40", 502),
               ("KASSEL", "Kassel (guia de atracagem)", 503), ("PERS", "Personalizado", 504)],
}
PARAMS = [
    ("TIPO_MF", "enum:TipoMF", "T30", "Tipo do meio-fio"),
    ("H_P",    "double", "0.3",    "Altura H"),
    ("LT_P",   "double", "0.12",   "Largura do topo LT"),
    ("LB_P",   "double", "0.142",  "Largura da base LB"),
    ("R_P",    "double", "0.05",   "Raio de concordância R"),
    ("SARJETA", "enumYN", "Yes",   "Sarjeta"),
    ("LS",     "double", "0.5",    "Largura da sarjeta"),
    ("iSa",    "grade",  "-0.1",   "Declividade da sarjeta"),
    ("ESar",   "double", "0.05",   "Espessura da sarjeta"),
    ("AMF_LIVRE",        "double", "0.18", "Altura livre"),
    ("EMB",    "double", "0.12",   "Enterrado mínimo"),
    ("ESTICAR", "enumYN", "No",    "Esticar altura"),
    ("QMF_CURVO", "enumYN", "No",  "Marcar MF em curva"),
    ("DESTAQUE", "double", "0",   "Destaque (codes)"),
    ("iRampa", "grade",  "0.0833", "Declividade máx. das rampas"),
    ("REB",    "double", "0",      "Rebaixo (profundidade)"),
    ("CALCADA", "enumYN", "No",    "Calçada"),
    ("LC",     "double", "1.5",    "Calçada: largura"),
    ("iC",     "grade",  "0.02",   "Calçada: declividade"),
    ("E_CALC", "double", "0.07",   "Calçada: espessura do piso"),
    ("E_CALC_BASE", "double", "0.05", "Calçada: espessura da base"),
    ("BERCO",  "enumYN", "Yes",   "Berço (camadas sob MF e sarjeta)"),
    ("I_PISTA", "grade", "-0.03", "Berço: declividade da pista"),
    ("E_REV",  "double", "0.3",    "Berço: espessura do revestimento"),
    ("E_BASE", "double", "0.2",    "Berço: espessura da base"),
    ("E_SUBBASE", "double", "0.2", "Berço: espessura da sub-base"),
    ("E_REF",  "double", "0.1",    "Berço: espessura do reforço"),
]
TARGETS = [  # (nome, display, preview, ativo no preview, tipo)
    ("FLOWLINE_ELEV", "Flowline",       "-0.05", True, "elev"),
    ("TOPO_ELEV",     "Topo do meio-fio", "0.13", True, "elev"),
    ("CALC_OFFSET",   "Calçada: borda externa (offset)", "2.5", False, "offset"),
    ("CALC_ELEV",     "Calçada: borda externa (cota)",   "0.2", False, "elev"),
]
OUTPUTS = [
    ("EXPOSTO",    "Altura exposta"),
    ("ENTERRADO",  "Altura enterrada"),
    ("H_REAL",     "Altura real"),
    ("ISA_REAL",   "Declividade real da sarjeta"),
    ("EMB_OK",     "Enterrado OK"),
    ("SARJETA_OK", "Sarjeta OK"),
    ("BERCO_OK",   "Berço OK (1 = MF não atravessa a base)"),
    ("RAMPA_OK",   "Rampa OK (1 = cabe na calçada com a decl. máx.)"),
    ("FAIXA_LIVRE", "Calçada: faixa livre após a rampa"),
]

# ----------------------------------------------------------------- expressoes
T30 = 'TIPO_MF.Value="T30"'; T40 = 'TIPO_MF.Value="T40"'; T50 = 'TIPO_MF.Value="KASSEL"'
def cat(t30, t40, kas, pers):  # valor por tipo de catálogo
    return f'[IF({T30},{t30},IF({T40},{t40},IF({T50},{kas},{pers})))]'
EXPR = {
    "H":  cat("0.30", "0.40", "0.40", "H_P"),
    "LT": cat("0.12", "0.12", "0.16", "LT_P"),
    "LB": cat("0.142", "0.15", "0.30", "LB_P"),
    "R":  cat("0.05", "0.05", "0.02", "R_P"),
    "FH": "[0.15]",
    "RC": "[0.08]",
    "KK": f"[IF({T50},1.0,0.0)]",
    "SJ": '[IF(SARJETA.Value="Yes" AndAlso KK<0.5,"Yes","No")]',
    "EXP0": "[IF(TOPO_ELEV.IsValid, TOPO_ELEV.Elevation - P2.Elevation, AMF_LIVRE)]",
    # Kassel tem dimensões fixas: exposto = H - pé, sem alvo, sem altura livre, sem esticar
    "EXPN": ('[IF(KK>0.5, H-FH, IF(ESTICAR.Value="Yes", Math.Max(0,EXP0), '
             'Math.Max(0, Math.Min(EXP0, H-0.001))))]'),
    # REB: profundidade do rebaixo (o MFREBAIXO grava por Corridor Transition). Kassel não rebaixa.
    "DREB": "[IF(KK>0.5, 0, Math.Max(0, Math.Min(REB, EXPN)))]",
    "EXP":  "[EXPN - DREB]",
    "HR":   '[IF(KK>0.5, H, Math.Max(0.001, IF(ESTICAR.Value="Yes", EXP+EMB, H)))]',
    "ENT":  "[HR-EXP]",
    "M":    "[IF(KK>0.5, 0, (LB-LT)/HR)]",
    "SW":   "[Math.PI/2 - Math.Atan(M)]",
    "TT":   "[Math.Min(R*Math.Tan(SW/2), LT-0.005)]",
    "MFTipoCode":   f'[IF(ESTICAR.Value="Yes" AndAlso KK<0.5,"MF_MOLDADO",IF({T30},"MF_T30",IF({T40},"MF_T40",IF({T50},"MF_KASSEL","MF_PERS"))))]',
    "MFStatusCode": '[IF(DREB > 0.005 OrElse EXP < IF(KK>0.5, H-FH, AMF_LIVRE) - 0.005,"MF_REBAIXADO","MF_NORMAL")]',
    "DatumMF":   '[IF(BERCO.Value="Yes","","Datum")]',
    "DatumBase": '[IF(E_SUBBASE<=0.0005,"Datum","")]',
    "DatumSub":  '[IF(E_REF<=0.0005,"Datum","")]',
    # DESTAQUE: o MFREBAIXO grava 1->2 (S0-S1), 2->3 (S1-S2), 3->4 (S2-S3) por Corridor Transition,
    # nas mesmas estacas da geometria. [1,4) = REBAIXO; 0 (fora) e 4 (em S3) = sem code.
    # Assim a hachura vai exatamente de S0 a S3.
    "Dst": '[IF(DESTAQUE >= 0.9995 AndAlso DESTAQUE < 3.9995, "REBAIXO", "")]',
    "MFCurvaCode":  '[IF(SA.IsLayout,"MF_RETO",IF(QMF_CURVO.Value="Yes" AndAlso Baseline.TurnDirection<>0,"MF_CURVO","MF_RETO"))]',
}
STRVARS = {"SJ", "Dst"}
VARTYPES = {k: ("String" if (k.endswith("Code") or k.startswith("Datum") or k in STRVARS) else "Double") for k in EXPR}

def arcdx(k):  # delta entre ponto k-1 e k do arco convexo do topo (4 segmentos)
    return f"[R*(Math.Cos(Math.PI/2+{k}*SW/4)-Math.Cos(Math.PI/2+{k-1}*SW/4))]"
def arcdy(k):
    return f"[R*(Math.Sin(Math.PI/2+{k}*SW/4)-Math.Sin(Math.PI/2+{k-1}*SW/4))]"
def ccdx(k):   # côncava do Kassel: de 0 a -90°, 3 segmentos
    return f"[RC*(Math.Cos(-{k}*Math.PI/6)-Math.Cos(-{k-1}*Math.PI/6))]"
def ccdy(k):
    return f"[RC*(Math.Sin(-{k}*Math.PI/6)-Math.Sin(-{k-1}*Math.PI/6))]"

# ----------------------------------------------------------------- passos
STEPS = []
def var(name):            STEPS.append(dict(kind="var", name=name))
def pt(num, frm, dx, dy, codes=(), link=None, lcodes=(), disp=None):
    STEPS.append(dict(kind="pt", num=num, frm=frm, dx=dx, dy=dy, codes=list(codes),
                      link=link, lcodes=list(lcodes), disp=disp))
def pt_slope(num, frm, slope, dx, elevtarget, codes=(), link=None, lcodes=(), disp=None, ot=""):
    STEPS.append(dict(kind="ptslope", num=num, frm=frm, slope=slope, dx=dx, et=elevtarget, ot=ot,
                      codes=list(codes), link=link, lcodes=list(lcodes), disp=disp))
def lk(num, a, b, codes=(), disp=None): STEPS.append(dict(kind="lk", num=num, a=a, b=b, codes=list(codes), disp=disp))
def sh(num, links, codes=(), disp=None): STEPS.append(dict(kind="sh", num=num, links=links, codes=list(codes), disp=disp))
def out(name, expr): STEPS.append(dict(kind="out", name=name, expr=expr))
class Switch:
    """bloco condicional multi-caso: with Switch(expr) as sw: / with sw.case("A"): ..."""
    def __init__(self, expr, disp=""):
        self.expr, self.disp, self.cases = expr, disp, []
    def __enter__(self): return self
    def __exit__(self, *a): STEPS.append(dict(kind="switch", sw=self))
    def case(self, key):
        sw = self
        class _C:
            def __enter__(self2):
                global STEPS; self2._p = STEPS; STEPS = []; return self2
            def __exit__(self2, *a):
                global STEPS; steps = STEPS; STEPS = self2._p; sw.cases.append((key, steps))
        return _C()

TOP = ["Curb", "Top", "TOPO"]
for v in ("H", "LT", "LB", "R", "FH", "RC", "KK", "SJ"): var(v)
pt("P1", None, "0", "0", codes=["Flange"], disp="P1 - borda da pista")
pt_slope("P2", "P1", "[iSa]", '[IF(SJ="Yes",LS,0)]', "[FLOWLINE_ELEV]", codes=["Flowline_Gutter"],
         disp="P2 - flowline (alvo cota)")
for v in ("EXP0", "EXPN", "DREB", "EXP", "HR", "ENT", "M", "SW", "TT", "MFTipoCode", "MFStatusCode", "MFCurvaCode",
          "DatumMF", "DatumBase", "DatumSub", "Dst"): var(v)
# sarjeta
with Switch('[SJ]', "Sarjeta?") as swSJ:
  with swSJ.case("Yes"):
    lk("L1", "P1", "P2", codes=["Sarjeta", "Top", "TOPO"], disp="L1 - topo da sarjeta")
    pt("P3", "P2", "[-M*ESar]", "[-ESar]", link="L2", lcodes=["Sarjeta"], disp="P3&L2 - fundo sarjeta na face do MF")
    pt("P4", "P1", "0", "[-ESar]", disp="P4 - fundo sarjeta na borda da pista")
    lk("L3", "P3", "P4", codes=["Sarjeta", "[DatumMF]"], disp="L3 - fundo da sarjeta")
    lk("L4", "P4", "P1", codes=["Sarjeta"], disp="L4 - lado da sarjeta (pista)")
    sh("S1", ["L1", "L2", "L3", "L4"], codes=["Sarjeta", "SARJETA", "[Dst]"], disp="S1 - SARJETA")
# meio-fio: parte comum (pé da face, fundo, face de trás)
pt("P5", "P2", "[-M*ENT]", "[-ENT]", disp="P5 - pé da face (fundo da frente)")
pt("P6", "P5", "[LB]", "0", link="L6", lcodes=["Curb", "[DatumMF]"], disp="P6&L6 - fundo de trás")
pt("P7", "P6", "0", "[HR]", codes=["Back_Curb", "[MFStatusCode]", "[MFCurvaCode]"],
   link="L7", lcodes=["Curb"], disp="P7&L7 - topo de trás (Back_Curb)")
with Switch('[IF(KK>0.5,"KASSEL","TRAPEZIO")]', "Forma") as swF:
  with swF.case("TRAPEZIO"):
    lk("L5", "P2", "P5", codes=["Curb"], disp="L5 - face enterrada")
    pt("P8", "P7", "[-(LT-TT)]", "0", codes=["Top_Curb"], link="L8", lcodes=TOP,
       disp="P8&L8 - início do arco (Top_Curb)")
    prev = "P8"
    for k in range(1, 5):
        n = f"P{8+k}"
        pt(n, prev, arcdx(k), arcdy(k), link=f"L{8+k}", lcodes=TOP,
           disp=f"{n}&L{8+k} - arco {k}/4" + (" (tangente na face)" if k == 4 else ""))
        prev = n
    lk("L13", "P12", "P2", codes=TOP, disp="L13 - face exposta")
    sh("S2", ["L5", "L6", "L7", "L8", "L9", "L10", "L11", "L12", "L13"],
       codes=["Curb", "[MFTipoCode]", "[MFStatusCode]", "[Dst]"], disp="S2 - MEIO-FIO (trapézio)")
  with swF.case("KASSEL"):
    pt("P13", "P7", "[-(LT-TT)]", "0", codes=["Top_Curb"], link="L14", lcodes=TOP,
       disp="P13&L14 - início do arco do topo (Top_Curb)")
    prev = "P13"
    for k in range(1, 5):
        n = f"P{13+k}"
        pt(n, prev, arcdx(k), arcdy(k), link=f"L{14+k}", lcodes=TOP, disp=f"{n}&L{14+k} - arco do topo {k}/4")
        prev = n
    pt("P18", "P17", "0", "[(P5.Elevation + FH + RC) - P17.Elevation]", link="L19", lcodes=TOP,
       disp="P18&L19 - face (início da côncava)")
    prev = "P18"
    for k in range(1, 4):
        n = f"P{18+k}"
        pt(n, prev, ccdx(k), ccdy(k), link=f"L{19+k}", lcodes=TOP, disp=f"{n}&L{19+k} - côncava {k}/3")
        prev = n
    pt("P22", "P21", "[-Math.Abs(P21.Offset-P5.Offset)]", "0", codes=["Curb_Toe"], link="L23", lcodes=TOP,
       disp="P22&L23 - topo do pé")
    lk("L24", "P22", "P5", codes=["Curb"], disp="L24 - frente do pé")
    sh("S3", ["L6", "L7", "L14", "L15", "L16", "L17", "L18", "L19", "L20", "L21", "L22", "L23", "L24"],
       codes=["Curb", "[MFTipoCode]", "[MFStatusCode]", "[Dst]"], disp="S3 - MEIO-FIO (Kassel)")

# O berço muda a QUANTIDADE de links conforme o MF desce (camada DENTRO/ABAIXO). Em planta o Civil 3D
# liga os links de uma seção à seguinte pela ordem; se o berço vier antes, os links de depois (calçada e
# sobreposição) trocam de posição entre S0 e S1 e a hachura sai errada. Por isso ele é gerado aqui mas
# entra no fluxo POR ÚLTIMO (ver _BERCO abaixo).
_B0 = len(STEPS)
# berço: camadas sob sarjeta + meio-fio, até a face de trás, RECORTADAS pelo meio-fio.
# Linhas das camadas: elev(d) = P1.Elevation - D + I_PISTA*d  (d = distância horizontal a partir de P1)
EXPR.update({
    "DP2": "[Math.Abs(P2.Offset-P1.Offset)]",
    "D5":  "[Math.Abs(P5.Offset-P1.Offset)]",
    "D6":  "[Math.Abs(P6.Offset-P1.Offset)]",
    "KF":  "[1 - I_PISTA*M]",
    "AF":  "[I_PISTA*(DP2 - M*P2.Elevation)]",
    "YB1": "[(P1.Elevation - (E_REV+E_BASE) + AF)/KF]",
    "YB2": "[(P1.Elevation - (E_REV+E_BASE+E_SUBBASE) + AF)/KF]",
    "YB3": "[(P1.Elevation - (E_REV+E_BASE+E_SUBBASE+E_REF) + AF)/KF]",
})
VARTYPES.update({k: "Double" for k in ("DP2","D5","D6","KF","AF","YB1","YB2","YB3")})
_n = {"p": 30, "l": 30, "s": 5}
def P(): _n["p"] += 1; return f"P{_n['p']}"
def L(): _n["l"] += 1; return f"L{_n['l']}"
def S(): _n["s"] += 1; return f"S{_n['s']}"
def face_pt(y_expr, disp):
    n = P(); pt(n, "P2", f"[M*({y_expr} - P2.Elevation)]", f"[{y_expr} - P2.Elevation]", disp=f"{n} - {disp}"); return n
def back_pt(D, disp):
    n = P(); pt(n, "P6", "0", f"[(P1.Elevation - ({D}) + I_PISTA*D6) - P6.Elevation]", disp=f"{n} - {disp}"); return n
def left_pt(D, disp):
    n = P(); pt(n, "P1", "0", f"[-({D})]", disp=f"{n} - {disp}"); return n
def ZF(D): return f"(P1.Elevation - ({D}) + I_PISTA*D5)"
def ZB(D): return f"(P1.Elevation - ({D}) + I_PISTA*D6)"
def left5_pt(D, disp):
    n = P(); pt(n, "P5", "0", f"[{ZF(D)} - P5.Elevation]", disp=f"{n} - {disp}"); return n
def layer_acima(code, datum, topLink, TL, TR, BL, Db, tag):
    BR = back_pt(Db, f"{tag}: fundo na face de trás")
    l1, l2, l3 = L(), L(), L()
    lk(l1, TR, BR, codes=[code], disp=f"{l1} {tag}: face de trás")
    lk(l2, BR, BL, codes=[code, datum], disp=f"{l2} {tag}: fundo")
    lk(l3, BL, TL, codes=[code], disp=f"{l3} {tag}: lado da pista")
    n = S(); sh(n, [topLink, l1, l2, l3], codes=[code], disp=f"{n} {tag} (MF acima)")
    return BR, l2
def layer_dentro(code, datum, topLink, TL, Ft, BL, Db, tag):
    BR = back_pt(Db, f"{tag}: fundo na face de trás")
    l1, l2, l3, l4 = L(), L(), L(), L()
    lk(l1, Ft, "P5", codes=[code], disp=f"{l1} {tag}: face do MF")
    lk(l2, "P6", BR, codes=[code], disp=f"{l2} {tag}: face de trás")
    lk(l3, BR, BL, codes=[code, datum], disp=f"{l3} {tag}: fundo")
    lk(l4, BL, TL, codes=[code], disp=f"{l4} {tag}: ETW")
    n = S(); sh(n, [topLink, l1, "L6", l2, l3, l4], codes=[code], disp=f"{n} {tag} (MF dentro)")
    return BR, l3
def layer_abaixo(code, datum, topLink, TL, Ft, BL, Yb, tag):
    Fb = face_pt(Yb, f"{tag}: face x fundo")
    l1, l2, l3 = L(), L(), L()
    lk(l1, Ft, Fb, codes=[code], disp=f"{l1} {tag}: face do MF")
    lk(l2, Fb, BL, codes=[code, datum], disp=f"{l2} {tag}: fundo")
    lk(l3, BL, TL, codes=[code], disp=f"{l3} {tag}: ETW")
    n = S(); sh(n, [topLink, l1, l2, l3], codes=[code], disp=f"{n} {tag} (MF atravessa)")
    return Fb, l2
def layer_sob_mf(code, datum, D, tag):
    """sem sarjeta: camada que contém o fundo do MF — topo = fundo do MF (L6), lado da pista na vertical do pé"""
    BL = left5_pt(D, f"{tag}: fundo sob o pé")
    BR = back_pt(D, f"{tag}: fundo na face de trás")
    l1, l2, l3 = L(), L(), L()
    lk(l1, "P6", BR, codes=[code], disp=f"{l1} {tag}: face de trás")
    lk(l2, BR, BL, codes=[code, datum], disp=f"{l2} {tag}: fundo")
    lk(l3, BL, "P5", codes=[code], disp=f"{l3} {tag}: lado da pista")
    n = S(); sh(n, ["L6", l1, l2, l3], codes=[code], disp=f"{n} {tag} (sob o MF)")
    return BL, BR, l2
D1 = "E_REV+E_BASE"; D2 = D1 + "+E_SUBBASE"; D3 = D2 + "+E_REF"
def cond(D): return f"Math.Min(P5.Elevation - {ZF(D)}, P6.Elevation - {ZB(D)}) > 0.0005"

with Switch('[IF(BERCO.Value="Yes","Yes","No")]', "BERCO?") as swB:
  with swB.case("Yes"):
    for v in ("DP2", "D5", "D6", "KF", "AF", "YB1", "YB2", "YB3"): var(v)
    with Switch('[SJ]', "Berço: com sarjeta?") as swBS:
      with swBS.case("Yes"):
        BL1 = left_pt(D1, "base: fundo no ETW")
        with Switch('[IF(P5.Elevation >= YB1, "DENTRO", "ABAIXO")]', "MF x base") as sw1:
          with sw1.case("DENTRO"):
            BR1, bot1 = layer_dentro("Base", "[DatumBase]", "L3", "P4", "P3", BL1, D1, "BASE")
            with Switch('[IF(E_SUBBASE>0.0005,"Yes","No")]', "E_SUBBASE>0?") as swS:
              with swS.case("Yes"):
                BL2 = left_pt(D2, "sub-base: fundo no ETW")
                BR2, bot2 = layer_acima("SubBase", "[DatumSub]", bot1, BL1, BR1, BL2, D2, "SUB-BASE")
                with Switch('[IF(E_REF>0.0005,"Yes","No")]', "E_REF>0?") as swR:
                  with swR.case("Yes"):
                    BL3 = left_pt(D3, "reforço: fundo no ETW")
                    layer_acima("SubGrade", "Datum", bot2, BL2, BR2, BL3, D3, "REFORÇO")
          with sw1.case("ABAIXO"):
            Fb1, bot1 = layer_abaixo("Base", "[DatumBase]", "L3", "P4", "P3", BL1, "YB1", "BASE")
            with Switch('[IF(E_SUBBASE>0.0005,"Yes","No")]', "E_SUBBASE>0?") as swS:
              with swS.case("Yes"):
                BL2 = left_pt(D2, "sub-base: fundo no ETW")
                with Switch('[IF(P5.Elevation >= YB2, "DENTRO", "ABAIXO")]', "MF x sub-base") as sw2:
                  with sw2.case("DENTRO"):
                    BR2, bot2 = layer_dentro("SubBase", "[DatumSub]", bot1, BL1, Fb1, BL2, D2, "SUB-BASE")
                    with Switch('[IF(E_REF>0.0005,"Yes","No")]', "E_REF>0?") as swR:
                      with swR.case("Yes"):
                        BL3 = left_pt(D3, "reforço: fundo no ETW")
                        layer_acima("SubGrade", "Datum", bot2, BL2, BR2, BL3, D3, "REFORÇO")
                  with sw2.case("ABAIXO"):
                    Fb2, bot2 = layer_abaixo("SubBase", "[DatumSub]", bot1, BL1, Fb1, BL2, "YB2", "SUB-BASE")
                    with Switch('[IF(E_REF>0.0005,"Yes","No")]', "E_REF>0?") as swR:
                      with swR.case("Yes"):
                        BL3 = left_pt(D3, "reforço: fundo no ETW")
                        with Switch('[IF(P5.Elevation >= YB3, "DENTRO", "ABAIXO")]', "MF x reforço") as sw3:
                          with sw3.case("DENTRO"):
                            layer_dentro("SubGrade", "Datum", bot2, BL2, Fb2, BL3, D3, "REFORÇO")
                          with sw3.case("ABAIXO"):
                            layer_abaixo("SubGrade", "Datum", bot2, BL2, Fb2, BL3, "YB3", "REFORÇO")
        out("BERCO_OK", "[IF(P5.Elevation >= YB1,1,0)]")
      with swBS.case("No"):
        # sem sarjeta: o MF ocupa toda a largura do berço; só sobra o que está abaixo do fundo dele
        with Switch(f'[IF({cond(D1)}, "BASE", IF(E_SUBBASE>0.0005 AndAlso {cond(D2)}, "SUB", '
                    f'IF(E_SUBBASE>0.0005 AndAlso E_REF>0.0005 AndAlso {cond(D3)}, "REF", "NADA")))]',
                    "Sem sarjeta: MF termina em qual camada?") as swN:
          with swN.case("BASE"):
            bl1, br1, b1 = layer_sob_mf("Base", "[DatumBase]", D1, "BASE")
            with Switch('[IF(E_SUBBASE>0.0005,"Yes","No")]', "E_SUBBASE>0?") as swS:
              with swS.case("Yes"):
                bl2 = left5_pt(D2, "sub-base: fundo sob o pé")
                br2, b2 = layer_acima("SubBase", "[DatumSub]", b1, bl1, br1, bl2, D2, "SUB-BASE")
                with Switch('[IF(E_REF>0.0005,"Yes","No")]', "E_REF>0?") as swR:
                  with swR.case("Yes"):
                    bl3 = left5_pt(D3, "reforço: fundo sob o pé")
                    layer_acima("SubGrade", "Datum", b2, bl2, br2, bl3, D3, "REFORÇO")
          with swN.case("SUB"):
            bl2, br2, b2 = layer_sob_mf("SubBase", "[DatumSub]", D2, "SUB-BASE")
            with Switch('[IF(E_REF>0.0005,"Yes","No")]', "E_REF>0?") as swR:
              with swR.case("Yes"):
                bl3 = left5_pt(D3, "reforço: fundo sob o pé")
                layer_acima("SubGrade", "Datum", b2, bl2, br2, bl3, D3, "REFORÇO")
          with swN.case("REF"):
            layer_sob_mf("SubGrade", "Datum", D3, "REFORÇO")
        out("BERCO_OK", f"[IF({cond(D1)},1,0)]")

_BERCO = STEPS[_B0:]; del STEPS[_B0:]

# calçada: começa no topo de trás do MF (P7). Borda externa na cota NORMAL (topo normal do MF +
# declividade), ou nos alvos; no rebaixo, rampa a partir do topo rebaixado com a declividade máxima
# (iRampa) até encontrar a calçada normal (lógica do pkt MF_TIPO10_RAMPA_ACESSIVEL).
EXPR.update({
    # Grade -> Double (IF misturando Grade e Double não compila)
    "IRD":  "[iRampa + 0.0]",
    "ICD":  "[iC + 0.0]",
    "W0":   "[IF(CALC_OFFSET.IsValid, Math.Max(0.05, Math.Abs(CALC_OFFSET.Offset - P7.Offset)), Math.Max(0.05, LC))]",
    "FIT0": "[IF(DREB <= 0.0005, 1, IF(IRD > ICD + 0.000001 AndAlso DREB/Math.Max(IRD - ICD, 0.000001) <= W0, 1, 0))]",
    "SLC":  "[IF(FIT0 > 0.5, ICD + DREB/W0, IRD)]",
    "WC":   "[Math.Max(0.05, Math.Abs(P102.Offset - P7.Offset))]",
    "SN":   "[(P102.Elevation - P7.Elevation - DREB)/WC]",
    "FIT":  "[IF(DREB <= 0.0005, 1, IF(IRD > SN + 0.000001 AndAlso DREB/Math.Max(IRD - SN, 0.000001) <= WC - 0.001, 1, 0))]",
    "XR":   "[IF(DREB <= 0.0005, 0.001, IF(FIT > 0.5, Math.Max(0.001, DREB/(IRD - SN)), WC - 0.001))]",
    "YR":   "[IF(DREB > 0.0005 AndAlso FIT > 0.5, IRD*XR, (P102.Elevation - P7.Elevation)*XR/WC)]",
    "DatumCP": '[IF(E_CALC_BASE <= 0.0005, "Datum", "")]',
})
VARTYPES.update({k: "Double" for k in ("IRD", "ICD", "W0", "FIT0", "SLC", "WC", "SN", "FIT", "XR", "YR")})
VARTYPES["DatumCP"] = "String"
with Switch('[IF(CALCADA.Value="Yes","Yes","No")]', "Calçada?") as swC:
  with swC.case("Yes"):
    for v in ("IRD", "ICD", "W0", "FIT0", "SLC"): var(v)
    pt_slope("P102", "P7", "[SLC]", "[W0]", "[CALC_ELEV]", codes=["Calcada_Ext"], ot="[CALC_OFFSET]",
             disp="P102 - borda externa da calçada (alvos)")
    for v in ("WC", "SN", "FIT", "XR", "YR", "DatumCP"): var(v)
    pt("P101", "P7", "[XR]", "[YR]", codes=["Rampa_Fim"], link="L101", lcodes=["Calcada", "Top", "TOPO"],
       disp="P101&L101 - fim da rampa (topo)")
    lk("L102", "P101", "P102", codes=["Calcada", "Top", "TOPO"], disp="L102 - topo da calçada")
    pt("P103", "P102", "0", "[-E_CALC]", link="L103", lcodes=["Calcada"], disp="P103&L103 - borda externa (piso)")
    pt("P104", "P101", "0", "[-E_CALC]", disp="P104 - fundo do piso no fim da rampa")
    pt("P105", "P7", "0", "[-E_CALC]", disp="P105 - fundo do piso no MF")
    lk("L104", "P103", "P104", codes=["Calcada", "[DatumCP]"], disp="L104 - fundo do piso")
    lk("L105", "P104", "P101", codes=["Calcada"], disp="L105 - divisa rampa/calçada")
    lk("L106", "P104", "P105", codes=["Calcada", "[DatumCP]"], disp="L106 - fundo do piso (rampa)")
    lk("L107", "P105", "P7", codes=["Calcada"], disp="L107 - encosto no MF")
    sh("S31", ["L101", "L105", "L106", "L107"], codes=["Calcada", "CALCADA", "[Dst]"], disp="S31 - CALÇADA (rampa)")
    sh("S32", ["L102", "L103", "L104", "L105"], codes=["Calcada", "CALCADA"], disp="S32 - CALÇADA")
    with Switch('[IF(E_CALC_BASE > 0.0005, "Yes", "No")]', "Base da calçada?") as swCB:
      with swCB.case("Yes"):
        pt("P106", "P103", "0", "[-E_CALC_BASE]", link="L108", lcodes=["CalcadaBase"], disp="P106&L108 - borda externa (base)")
        pt("P107", "P105", "0", "[-E_CALC_BASE]", disp="P107 - fundo da base no MF")
        lk("L109", "P106", "P107", codes=["CalcadaBase", "Datum"], disp="L109 - fundo da base")
        lk("L110", "P107", "P105", codes=["CalcadaBase"], disp="L110 - encosto da base no MF")
        sh("S33", ["L104", "L106", "L108", "L109", "L110"], codes=["CalcadaBase"], disp="S33 - BASE DA CALÇADA")
    out("RAMPA_OK", "[FIT]")
    out("FAIXA_LIVRE", "[Math.Max(0, WC - IF(DREB > 0.0005, XR, 0))]")

# sobreposição: links só com o code do rebaixo, criados por último (desenham por cima) e sempre
# presentes (sem code fora do rebaixo), para a faixa entre seções fechar exatamente em S0..S3.
with Switch('[SJ]', "Sobreposição: sarjeta") as swOS:
  with swOS.case("Yes"):
    lk("L201", "P1", "P2", codes=["[Dst]"], disp="L201 - sobreposição: topo da sarjeta")
with Switch('[IF(KK>0.5,"KASSEL","TRAPEZIO")]', "Sobreposição: MF") as swOF:
  with swOF.case("TRAPEZIO"):
    chainT = ["P7", "P8", "P9", "P10", "P11", "P12", "P2"]
    for i in range(len(chainT) - 1):
        lk(f"L{211+i}", chainT[i], chainT[i+1], codes=["[Dst]"], disp=f"L{211+i} - sobreposição: topo do MF")
  with swOF.case("KASSEL"):
    chainK = ["P7", "P13", "P14", "P15", "P16", "P17", "P18", "P19", "P20", "P21", "P22"]
    for i in range(len(chainK) - 1):
        lk(f"L{221+i}", chainK[i], chainK[i+1], codes=["[Dst]"], disp=f"L{221+i} - sobreposição: topo do MF")
with Switch('[IF(CALCADA.Value="Yes","Yes","No")]', "Sobreposição: calçada") as swOC:
  with swOC.case("Yes"):
    lk("L241", "P7", "P101", codes=["[Dst]"], disp="L241 - sobreposição: rampa da calçada")

STEPS.extend(_BERCO)

# saidas
out("EXPOSTO", "[EXP]")
out("ENTERRADO", "[ENT]")
out("H_REAL", "[HR]")
out("ISA_REAL", '[IF(SJ="Yes",(P2.Elevation-P1.Elevation)/Math.Max(LS,0.001),0)]')
out("EMB_OK", "[IF(ENT>=EMB-0.0005,1,0)]")
out("SARJETA_OK", '[IF(SJ="No",1,IF(P2.Elevation<P1.Elevation-0.0005,1,0))]')

# ----------------------------------------------------------------- XAML
VS_EXP = ('<sap:WorkflowViewStateService.ViewState><scg:Dictionary x:TypeArguments="x:String, x:Object">'
          '<x:Boolean x:Key="IsExpanded">True</x:Boolean></scg:Dictionary></sap:WorkflowViewStateService.ViewState>')
COMMON = 'Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" ShowErrors="True" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]"'

def codelist(tag, codes):
    if not codes: return ""
    items = "".join(f'<InArgument x:TypeArguments="x:String">{escape(c)}</InArgument>' for c in codes)
    return f'<{tag}><scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">{items}</scg:List></{tag}>'

aid = [0]
def nextid():
    aid[0] += 1; return aid[0]

def activity(st):
    k = st["kind"]
    if k == "var":
        n = st["name"]; t = VARTYPES[n]; tx = "x:Double" if t == "Double" else "x:String"
        return (f'<asa1:InternalVariableDefine ActivityId="{nextid()}" DisplayName="{q(n)} &lt;{t}&gt;" '
                f'sap:VirtualizedContainerService.HintSize="200,22" OldVariableName="" ShowErrors="True" '
                f'SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]" '
                f'VariableName="{n}" VariableType="{t}">'
                f'<asa1:InternalVariableDefine.DefaultValue><InArgument x:TypeArguments="{tx}">{escape(EXPR[n])}</InArgument></asa1:InternalVariableDefine.DefaultValue>'
                f'<asa1:InternalVariableDefine.Variable><OutArgument x:TypeArguments="{tx}">[{n}]</OutArgument></asa1:InternalVariableDefine.Variable>'
                f'{VS_EXP}</asa1:InternalVariableDefine>')
    if k in ("pt", "ptslope"):
        disp = st["disp"] or st["num"]
        auto = st["link"] is not None
        attrs = (f'ActivityId="{nextid()}" ApplyAOR="False" AutoLink="{auto}" '
                 + (f'AutoLinkGeometryName="{st["link"]}" ' if auto else 'AutoLinkCodes="{x:Null}" AutoLinkGeometryName="{x:Null}" ')
                 + f'DisplayName="{q(disp)}" '
                 + (f'FromPoint="{st["frm"]}" ' if st["frm"] else 'FromPoint="{x:Null}" ')
                 + (('PointCodes="{x:Null}" ') if not st["codes"] else '')
                 + f'{COMMON} PointNumber="{st["num"]}" Positioning="{"SlopeAndDeltaX" if k=="ptslope" else "DeltaXAndDeltaY"}" Side="[Side]"')
        if k == "pt":
            args = (f'<InArgument x:TypeArguments="x:Double" x:Key="DeltaX1">{escape(st["dx"])}</InArgument>'
                    f'<InArgument x:TypeArguments="x:Double" x:Key="DeltaY1">{escape(st["dy"])}</InArgument>')
        else:
            OTX = (f'<InArgument x:TypeArguments="asw:OffsetTarget" x:Key="OffsetTarget2">{escape(st["ot"])}</InArgument>'
                   if st["ot"] else '<InArgument x:TypeArguments="asw:OffsetTarget" x:Key="OffsetTarget2" />')
            args = (f'<InArgument x:TypeArguments="x:Double" x:Key="Slope2">{escape(st["slope"])}</InArgument>'
                    f'<InArgument x:TypeArguments="x:Double" x:Key="DeltaX2">{escape(st["dx"])}</InArgument>'
                    + OTX +
                    f'<InArgument x:TypeArguments="asw:ElevationTarget" x:Key="ElevationTarget2">{escape(st["et"])}</InArgument>'
                    f'<InArgument x:TypeArguments="asw:CrossSegmentType" x:Key="Superelevation2" />')
        body = f'<asa1:CreatePoint.Arguments>{args}</asa1:CreatePoint.Arguments>'
        if auto: body += codelist("asa1:CreatePoint.AutoLinkCodes", st["lcodes"])
        body += codelist("asa1:CreatePoint.PointCodes", st["codes"])
        return f'<asa1:CreatePoint {attrs}>{body}{VS_EXP}</asa1:CreatePoint>'
    if k == "lk":
        disp = st["disp"] or st["num"]
        return (f'<asa1:CreateLink ActivityId="{nextid()}" ApplyAOR="False" DisplayName="{q(disp)}" EndPoint="{st["b"]}" '
                f'{COMMON} IsEnabled="True" LinkNumber="{st["num"]}" StartPoint="{st["a"]}">'
                f'{codelist("asa1:CreateLink.LinkCodes", st["codes"])}{VS_EXP}</asa1:CreateLink>')
    if k == "sh":
        disp = st["disp"] or st["num"]
        comp = "".join(f"<x:String>{l}</x:String>" for l in st["links"])
        return (f'<asa1:CreateShape ActivityId="{nextid()}" DisplayName="{q(disp)}" {COMMON} Links="{",".join(st["links"])}" ShapeNumber="{st["num"]}">'
                f'<asa1:CreateShape.ComponentNames>{comp}</asa1:CreateShape.ComponentNames>'
                f'{codelist("asa1:CreateShape.ShapeCodes", st["codes"])}{VS_EXP}</asa1:CreateShape>')
    if k == "out":
        return (f'<asa1:SetOutputParameterValue ActivityId="{nextid()}" DisplayName="{q(st["name"]+" = "+st["expr"])}" '
                f'sap:VirtualizedContainerService.HintSize="200,22" ShowErrors="True" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">'
                f'<asa1:SetOutputParameterValue.OutputParammeter><OutArgument x:TypeArguments="x:Double">[{st["name"]}]</OutArgument></asa1:SetOutputParameterValue.OutputParammeter>'
                f'<asa1:SetOutputParameterValue.Value><InArgument x:TypeArguments="x:Double">{escape(st["expr"])}</InArgument></asa1:SetOutputParameterValue.Value>'
                f'{VS_EXP}</asa1:SetOutputParameterValue>')
    raise ValueError(k)

# cadeia de FlowSteps aninhados (com FlowSwitch para blocos condicionais)
NAMES = []
ypos = [29]
def vs_step(x, y, last):
    con = '' if last else f'<av:PointCollection x:Key="ConnectorLocation">{x+100},{y+22} {x+100},{y+50}</av:PointCollection>'
    return (f'<sap:WorkflowViewStateService.ViewState><scg:Dictionary x:TypeArguments="x:String, x:Object">'
            f'<av:Point x:Key="ShapeLocation">{x},{y}</av:Point><av:Size x:Key="ShapeSize">200,22</av:Size>{con}'
            f'</scg:Dictionary></sap:WorkflowViewStateService.ViewState>')
def build(steps, cont, x):
    """steps -> XAML de cadeia aninhada. cont = None | ("define", name, xml) | ("ref", name)."""
    if not steps:
        if not cont: return ''
        return cont[2] if cont[0] == "define" else f'<x:Reference>{cont[1]}</x:Reference>'
    st = steps[0]; rest = steps[1:]
    if st["kind"] == "switch":
        sw = st["sw"]
        swname = f"__ReferenceID{len(NAMES)+1}"; NAMES.append(swname)
        y = ypos[0]; ypos[0] += 100
        if rest:
            rest_xml = build(rest, cont, x)
            rest_name = rest_xml.split('x:Name="', 1)[1].split('"', 1)[0]
            cont2 = ("define", rest_name, rest_xml)
        else:
            cont2 = cont
        cases_xml = []
        for i, (key, csteps) in enumerate(sw.cases):
            assert csteps, f"caso vazio {key}"
            c = cont2 if i == 0 else (("ref", cont2[1]) if cont2 else None)
            cx = build(csteps, c, x + 260 * (i + 1))
            tag = cx.split(' ', 1)[0]          # elemento raiz do caso (<FlowStep ou <FlowSwitch)
            assert tag in ('<FlowStep', '<FlowSwitch'), tag
            cases_xml.append(tag + f' x:Key="{key}"' + cx[len(tag):])
        cont_name = cont2[1] if cont2 else None
        default = f' Default="{{x:Reference {cont_name}}}"' if cont_name else ''
        vs = (f'<sap:WorkflowViewStateService.ViewState><scg:Dictionary x:TypeArguments="x:String, x:Object">'
              f'<x:Boolean x:Key="IsExpanded">True</x:Boolean><av:Point x:Key="ShapeLocation">{x+65},{y}</av:Point>'
              f'<av:Size x:Key="ShapeSize">70,87</av:Size></scg:Dictionary></sap:WorkflowViewStateService.ViewState>')
        return (f'<FlowSwitch x:TypeArguments="x:String"{default} x:Name="{swname}" Expression="{q(sw.expr)}" '
                f'sap:VirtualizedContainerService.HintSize="70,87">{vs}{"".join(cases_xml)}</FlowSwitch>')
    name = f"__ReferenceID{len(NAMES)+1}"; NAMES.append(name)
    y = ypos[0]; ypos[0] += 50
    nxt_xml = build(rest, cont, x)
    last = not nxt_xml
    nxt = f'<FlowStep.Next>{nxt_xml}</FlowStep.Next>' if nxt_xml else ''
    return f'<FlowStep x:Name="{name}">{vs_step(x, y, last)}{activity(st)}{nxt}</FlowStep>'

sys.setrecursionlimit(10000)
chain = build(STEPS, None, 40)
refs = "".join(f"<x:Reference>{n}</x:Reference>" for n in NAMES)

# atributos raiz (defaults)
rootattrs = []
for n, d, pv, on, tk in TARGETS:
    cls = "PreviewOffsetTarget" if tk == "offset" else "PreviewElevationTarget"
    rootattrs.append(f'this:Subassembly.{n}="[new {cls}(&quot;{n}&quot;, {on}, {pv})]"')
rootattrs.append('this:Subassembly.Side="[new EnumType(0, &quot;Right&quot;, &quot;Side&quot;)]"')
for n, t, d, disp in PARAMS:
    if t in ("double", "string"): rootattrs.append(f'this:Subassembly.{n}="{q(d)}"')
    elif t == "grade": rootattrs.append(f'this:Subassembly.{n}="[new Grade({d})]"')
    elif t == "enumYN": rootattrs.append(f'this:Subassembly.{n}="[new EnumType({10 if d=="Yes" else 11}, &quot;{d}&quot;)]"')
    elif t.startswith("enum:"):
        g = t[5:]; key = dict((i[0], i[2]) for i in ENUM_GROUPS[g])[d]
        rootattrs.append(f'this:Subassembly.{n}="[new EnumType({key}, &quot;{d}&quot;, &quot;{g}&quot;)]"')

members = ['<x:Property Name="Geometry" Type="InOutArgument(asw:Geometry)" />',
           '<x:Property Name="SubassemblyErrorCenter" Type="InOutArgument(asw:SubassemblyErrorCenter)" />',
           '<x:Property Name="SubassemblyRunMode" Type="InOutArgument(asw:SubassemblyRunMode)" />']
for n, disp, pv, on, tk in TARGETS:
    members.append(f'<x:Property Name="{n}" Type="InArgument(asw:{"OffsetTarget" if tk == "offset" else "ElevationTarget"})"><x:Property.Attributes>'
                   f'<asw:DisplayName2Attribute DisplayName="{q(disp)}" /><asw:EnabledFlag2Attribute EnabledFlag="True" />'
                   f'</x:Property.Attributes></x:Property>')
members.append('<x:Property Name="Side" Type="InArgument(asw:EnumType)"><x:Property.Attributes><asw:EnabledFlag2Attribute EnabledFlag="True" /></x:Property.Attributes></x:Property>')
for n, t, d, disp in PARAMS:
    ty = "asw:EnumType" if t.startswith("enum") else {"double": "x:Double", "grade": "asw:Grade", "string": "x:String"}[t]
    members.append(f'<x:Property Name="{n}" Type="InArgument({ty})"><x:Property.Attributes>'
                   f'<asw:EnabledFlag2Attribute EnabledFlag="True" /><asw:DisplayName2Attribute DisplayName="{q(disp)}" />'
                   f'</x:Property.Attributes></x:Property>')
for n, disp in OUTPUTS:
    members.append(f'<x:Property Name="{n}" Type="OutArgument(x:Double)"><x:Property.Attributes>'
                   f'<asw:EnabledFlag2Attribute EnabledFlag="True" /><asw:DisplayName2Attribute DisplayName="{q(disp)}" />'
                   f'</x:Property.Attributes></x:Property>')

variables = "".join(f'<Variable x:TypeArguments="x:{VARTYPES[n]}" Name="{n}" />' for n in EXPR)
ENUMS = [("AwayFromCrown",41),("Left",1),("LeftInsideLane",20),("LeftInsideShoulder",21),("LeftOutsideLane",22),
         ("LeftOutsideShoulder",23),("No",11),("None",-1),("Right",0),("RightInsideLane",24),("RightInsideShoulder",25),
         ("RightOutsideLane",26),("RightOutsideShoulder",27),("Supported",30),("TowardsCrown",40),("Unsupported",31),("Yes",10)]
for _g, _items in ENUM_GROUPS.items():
    ENUMS += [(i[0], i[2]) for i in _items]
ENUMS.sort(key=lambda e: e[0].lower())
variables += "".join(f'<Variable x:TypeArguments="asw:EnumType" Default="[new EnumType({v}, &quot;{n}&quot;)]" Modifiers="ReadOnly" Name="{n}" />' for n, v in ENUMS)

xaml = f'''﻿<Activity mc:Ignorable="sads sap" x:Class="Subassembly" {" ".join(rootattrs)}
 xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
 xmlns:asa="clr-namespace:Autodesk.SubassemblyComposer.API;assembly=Subassembly.API"
 xmlns:asa1="clr-namespace:Autodesk.SubassemblyComposer.ActivityLibrary;assembly=Subassembly.ActivityLibrary"
 xmlns:asa2="clr-namespace:Autodesk.SubassemblyComposer.API;assembly=Subassembly.WorkflowEngine"
 xmlns:asw="clr-namespace:Autodesk.SubassemblyComposer.WorkflowEngine;assembly=Subassembly.WorkflowEngine"
 xmlns:av="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
 xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
 xmlns:mva="clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities"
 xmlns:s="clr-namespace:System;assembly=mscorlib"
 xmlns:s1="clr-namespace:System;assembly=System.Core"
 xmlns:s2="clr-namespace:System;assembly=System"
 xmlns:s3="clr-namespace:System;assembly=System.Runtime.WindowsRuntime"
 xmlns:s4="clr-namespace:System;assembly=System.ServiceModel"
 xmlns:sa="clr-namespace:System.Activities;assembly=System.Activities"
 xmlns:sads="http://schemas.microsoft.com/netfx/2010/xaml/activities/debugger"
 xmlns:sap="http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation"
 xmlns:scg="clr-namespace:System.Collections.Generic;assembly=mscorlib"
 xmlns:this="clr-namespace:"
 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:Members>{"".join(members)}</x:Members>
  <mva:VisualBasic.Settings>Assembly references and imported namespaces serialized as XML namespaces</mva:VisualBasic.Settings>
  <Flowchart sap:VirtualizedContainerService.HintSize="684,636" mva:VisualBasic.Settings="Assembly references and imported namespaces serialized as XML namespaces">
    <Flowchart.Variables>{variables}</Flowchart.Variables>
    <sap:WorkflowViewStateService.ViewState><scg:Dictionary x:TypeArguments="x:String, x:Object">
      <x:Boolean x:Key="IsExpanded">False</x:Boolean><av:Point x:Key="ShapeLocation">110,2.5</av:Point><av:Size x:Key="ShapeSize">60,75</av:Size>
      <av:PointCollection x:Key="ConnectorLocation">140,77.5 140,29</av:PointCollection><x:Double x:Key="Width">670</x:Double>
    </scg:Dictionary></sap:WorkflowViewStateService.ViewState>
    <Flowchart.StartNode>{chain}</Flowchart.StartNode>
    {refs}
  </Flowchart>
</Activity>'''

# ----------------------------------------------------------------- .atc
def atcparam(n, t, d, disp):
    if t == "double": return f'<{n} DataType="double" TypeInfo="16" DisplayName="{q(disp)}" Description="{n}">{d}</{n}>'
    if t == "string": return f'<{n} DataType="string" TypeInfo="16" DisplayName="{q(disp)}" Description="{n}">{escape(d)}</{n}>'
    if t == "grade":  return f'<{n} DataType="double" TypeInfo="9" DisplayName="{q(disp)}" Description="{n}">{d}</{n}>'
    if t.startswith("enum:"):
        items = ENUM_GROUPS[t[5:]]; key = dict((i[0], i[2]) for i in items)[d]
        en = "".join(f'<{i[0]} DisplayName="{q(i[1])}">{i[2]}</{i[0]}>' for i in items)
        return f'<{n} DataType="long" TypeInfo="16" DisplayName="{q(disp)}" Description="{n}">{key}<Enum>{en}</Enum></{n}>'
    return (f'<{n} DataType="long" TypeInfo="16" DisplayName="{q(disp)}" Description="{n}">{10 if d=="Yes" else 11}'
            f'<Enum><Yes DisplayName="Yes">10</Yes><No DisplayName="No">11</No></Enum></{n}>')
atc = f'''<?xml version="1.0"?>
<Category xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <ItemID idValue="{{{uuid.uuid4()}}}" />
  <Properties>
    <ItemName>Category1</ItemName>
    <Images>
      <Image cx="93" cy="123" />
    </Images>
  </Properties>
  <CustomData />
  <Source />
  <Palettes />
  <Packages />
  <Tools>
    <Tool Name="{NAME}">
      <ItemID idValue="{{{uuid.uuid4()}}}" />
      <Properties>
        <ItemName>{NAME}</ItemName>
        <Images>
          <Image cx="64" cy="64" />
        </Images>
        <ToolTip>Version: {VERSION}</ToolTip>
        <Help>
          <HelpFile />
          <HelpCommand />
          <HelpData />
        </Help>
      </Properties>
      <Source />
      <StockToolRef idValue="{{7F55AAC0-0256-48D7-BFA5-914702663FDE}}" />
      <Data>
        <AeccDbSubassembly>
          <GeometryGenerateMode>UseDotNet</GeometryGenerateMode>
          <DotNetClass Assembly="{GUID}.xaml">Subassembly.{NAME}</DotNetClass>
          <Version>{VERSION}</Version>
          <Params>
            <Side DataType="long" TypeInfo="16" DisplayName="Side" Description="Side">0<Enum><Left DisplayName="Left">1</Left><Right DisplayName="Right">0</Right></Enum></Side>
            {chr(10).join("            "+atcparam(*p) for p in PARAMS).strip()}
          </Params>
        </AeccDbSubassembly>
        <Units>m</Units>
      </Data>
    </Tool>
  </Tools>
  <StockTools />
</Category>'''

emd = f'''<?xml version="1.0"?>
<EnumData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <EnumDatas>
    <Groups>
{"".join(f'      <EnumGroup Name="{g}">' + chr(10) + '        <Items>' + chr(10) + "".join(f'          <EnumItem Name="{i[0]}" DisplayName="{q(i[1])}" Key="{i[2]}" />' + chr(10) for i in items) + '        </Items>' + chr(10) + '      </EnumGroup>' + chr(10) for g, items in ENUM_GROUPS.items())}    </Groups>
    <DefinedVariables>
{chr(10).join(f"      <string>{n}</string>" for n in EXPR)}
    </DefinedVariables>
  </EnumDatas>
</EnumData>'''
cfg = '''<?xml version="1.0"?>
<Configuration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <CreatedWith>
    <ProductName>Autodesk Subassembly Composer</ProductName>
    <Version>ForVail</Version>
    <VersionNumber>13.8.1516.0</VersionNumber>
  </CreatedWith>
</Configuration>'''
cdmd = '''<?xml version="1.0"?>
<CodeData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <CodeItems />
</CodeData>'''
pvd = '''<?xml version="1.0"?>
<PreviewData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <Superelevation>
    <CrossSlopes>
''' + "".join(f'      <PreviewCrossSlope CrossSegmentType="{c}" Slope="{s}" IsDefined="true" />\n'
              for c, s in [("LeftInsideLane",-0.02),("LeftInsideShoulder",-0.05),("LeftOutsideLane",-0.02),("LeftOutsideShoulder",-0.05),
                           ("RightInsideLane",-0.02),("RightInsideShoulder",-0.05),("RightOutsideLane",-0.02),("RightOutsideShoulder",-0.05)]) + '''    </CrossSlopes>
  </Superelevation>
  <Cant>
    <CantParams>
      <PreviewCantParam Name="CantPivotType" Value="CenterLine" />
      <PreviewCantParam Name="LeftRail" Value="" />
      <PreviewCantParam Name="LeftRailDeltaElevation" Value="0" />
      <PreviewCantParam Name="RightRail" Value="" />
      <PreviewCantParam Name="RightRailDeltaElevation" Value="0" />
    </CantParams>
  </Cant>
</PreviewData>'''
ctypes = ('﻿<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
          '<Default Extension="atc" ContentType="" /><Default Extension="cfg" ContentType="" /><Default Extension="xaml" ContentType="" />'
          '<Default Extension="pvd" ContentType="" /><Default Extension="emd" ContentType="" /><Default Extension="cdmd" ContentType="" /></Types>')

import xml.etree.ElementTree as ET
ET.fromstring(xaml.encode("utf-8")); ET.fromstring(atc.encode()); ET.fromstring(emd.encode())
with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
    z.writestr(f"{GUID}.atc", atc.encode("utf-8"))
    z.writestr("[Content_Types].xml", ctypes.encode("utf-8"))
    z.writestr(f"{GUID}.cfg", cfg.encode("utf-8"))
    z.writestr(f"{GUID}.xaml", xaml.encode("utf-8"))
    z.writestr(f"{GUID}.pvd", pvd.encode("utf-8"))
    z.writestr(f"{GUID}.emd", emd.encode("utf-8"))
    z.writestr(f"{GUID}.cdmd", cdmd.encode("utf-8"))
print("ok", OUT, "steps:", len(STEPS))
