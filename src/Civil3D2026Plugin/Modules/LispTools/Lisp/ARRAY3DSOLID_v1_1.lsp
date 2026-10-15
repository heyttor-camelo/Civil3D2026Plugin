;;@TITULO  Copiar solido nos vertices de 3DPOLY
;;@CATEGORIA 3D
;;@ICONE solido3d
;;@COMANDO ARRAY3DSOLID
;;@COMANDO ARRAY3DSOLIDHELP
;;@VERSAO  1.1
;;@DESC    Copia um 3D SOLID para cada vertice de uma 3DPOLY, girando so em torno de Z para alinhar a
;;@DESC    direcao longitudinal do solido com a tangente da polilinha em planta. O solido nunca e inclinado
;;@DESC    e o original e mantido.
;;@USO     Digite ARRAY3DSOLID e selecione o solido modelo
;;@USO     Clique o ponto-base e um segundo ponto na direcao longitudinal
;;@USO     Selecione a 3DPOLY
;; ----------------------------------------------------------------
; ==================================================================================================
; ARRAY3DSOLID.LSP
; --------------------------------------------------------------------------------------------------
; OBJETIVO
;   Distribuir cópias de um 3D SOLID nos vértices de uma 3DPOLY.
;
; COMO FUNCIONA
;   1) O usuário seleciona um 3D SOLID modelo.
;   2) Informa um ponto-base no sólido.
;   3) Informa um segundo ponto que define a DIREÇÃO LONGITUDINAL do sólido.
;      Essa direção deve ser normal à face do sólido que deverá permanecer transversal à 3DPOLY.
;   4) Seleciona uma 3DPOLY.
;   5) Para cada vértice, a rotina calcula a tangente local 3D:
;        - primeira/última estação de uma polilinha aberta: direção do segmento adjacente;
;        - vértices internos: bissetriz entre os segmentos de entrada e saída;
;        - polilinha fechada: bissetriz cíclica em todos os vértices.
;   6) O sólido é copiado para o vértice e rotacionado SOMENTE NO PLANO XY, em torno do eixo Z.
;      A direção longitudinal do sólido é alinhada à projeção XY da tangente local da 3DPOLY.
;      A inclinação da 3DPOLY em Z não inclina o sólido.
;
; REGRAS / CRITÉRIOS
;   - Trabalha com AcDb3dPolyline (POLYLINE com flag 3D).
;   - O sólido original NÃO é apagado.
;   - Vértices coincidentes/degenerados são tratados com fallback para o segmento disponível.
;   - A orientação considera SOMENTE a projeção XY da 3DPOLY.
;   - O sólido nunca é inclinado: a rotação ocorre exclusivamente em torno do eixo Z global.
;   - O layer/propriedades do sólido modelo são preservados pelas cópias.
;
; COMANDOS
;   ARRAY3DSOLID      -> executa a rotina.
;   ARRAY3DSOLIDHELP  -> mostra ajuda resumida no AutoCAD.
; ==================================================================================================

(vl-load-com)

(setq *A3S-EPS* 1.0e-10)

(defun A3S:v+ (a b)
  (mapcar '+ a b)
)

(defun A3S:v- (a b)
  (mapcar '- a b)
)

(defun A3S:v* (v s)
  (mapcar '(lambda (x) (* x s)) v)
)

(defun A3S:dot (a b)
  (+ (* (car a) (car b))
     (* (cadr a) (cadr b))
     (* (caddr a) (caddr b)))
)

(defun A3S:cross (a b)
  (list
    (- (* (cadr a) (caddr b)) (* (caddr a) (cadr b)))
    (- (* (caddr a) (car b))  (* (car a) (caddr b)))
    (- (* (car a) (cadr b))   (* (cadr a) (car b)))
  )
)

(defun A3S:len (v)
  (sqrt (A3S:dot v v))
)

(defun A3S:unit (v / l)
  (setq l (A3S:len v))
  (if (> l *A3S-EPS*)
    (A3S:v* v (/ 1.0 l))
    nil
  )
)

(defun A3S:xy-unit (v / x y l)
  ; Normaliza somente a projeção XY e força Z = 0.
  (setq x (car v)
        y (cadr v)
        l (sqrt (+ (* x x) (* y y))))
  (if (> l *A3S-EPS*)
    (list (/ x l) (/ y l) 0.0)
    nil
  )
)

(defun A3S:clamp (x lo hi)
  (cond
    ((< x lo) lo)
    ((> x hi) hi)
    (T x)
  )
)

(defun A3S:pt+vec (p v)
  (list (+ (car p) (car v))
        (+ (cadr p) (cadr v))
        (+ (caddr p) (caddr v)))
)

(defun A3S:3dpoly-p (e / d flg)
  (and
    e
    (setq d (entget e))
    (= (cdr (assoc 0 d)) "POLYLINE")
    (setq flg (cdr (assoc 70 d)))
    (= 8 (logand flg 8))
    (= 0 (logand flg 16))
    (= 0 (logand flg 64))
  )
)

(defun A3S:closed-p (e / d flg)
  (setq d (entget e)
        flg (cdr (assoc 70 d)))
  (= 1 (logand flg 1))
)

(defun A3S:get-verts (pl / e d pts)
  (setq e (entnext pl)
        pts '())
  (while e
    (setq d (entget e))
    (cond
      ((= (cdr (assoc 0 d)) "VERTEX")
       (if (assoc 10 d)
         (setq pts (cons (cdr (assoc 10 d)) pts))
       )
      )
      ((= (cdr (assoc 0 d)) "SEQEND")
       (setq e nil)
      )
    )
    (if e (setq e (entnext e)))
  )
  (reverse pts)
)

(defun A3S:list-nth-wrap (lst i / n)
  (setq n (length lst))
  (nth (rem (+ i n) n) lst)
)

(defun A3S:tangent-open (pts i / n p pm pp vin vout s)
  ; Tangente local calculada SOMENTE em XY.
  (setq n (length pts)
        p (nth i pts))
  (cond
    ((= i 0)
     (A3S:xy-unit (A3S:v- (nth 1 pts) p))
    )
    ((= i (1- n))
     (A3S:xy-unit (A3S:v- p (nth (1- i) pts)))
    )
    (T
     (setq pm   (nth (1- i) pts)
           pp   (nth (1+ i) pts)
           vin  (A3S:xy-unit (A3S:v- p pm))
           vout (A3S:xy-unit (A3S:v- pp p)))
     (cond
       ((and vin vout
             (setq s (A3S:xy-unit (A3S:v+ vin vout))))
        s
       )
       (vout vout)
       (vin vin)
       (T nil)
     )
    )
  )
)

(defun A3S:tangent-closed (pts i / n p pm pp vin vout s)
  ; Tangente local calculada SOMENTE em XY.
  (setq n    (length pts)
        p    (nth i pts)
        pm   (A3S:list-nth-wrap pts (1- i))
        pp   (A3S:list-nth-wrap pts (1+ i))
        vin  (A3S:xy-unit (A3S:v- p pm))
        vout (A3S:xy-unit (A3S:v- pp p)))
  (cond
    ((and vin vout
          (setq s (A3S:xy-unit (A3S:v+ vin vout))))
     s
    )
    (vout vout)
    (vin vin)
    (T nil)
  )
)

(defun A3S:angle-xy (v)
  (atan (cadr v) (car v))
)

(defun A3S:rotate-xy-to-xy (obj origin from to / f tvec angFrom angTo ang)
  ; Rotação EXCLUSIVAMENTE em torno do eixo Z global.
  ; Nenhuma rotação em X/Y é aplicada ao sólido.
  (setq f    (A3S:xy-unit from)
        tvec (A3S:xy-unit to))
  (if (and f tvec)
    (progn
      (setq angFrom (A3S:angle-xy f)
            angTo   (A3S:angle-xy tvec)
            ang     (- angTo angFrom))
      ; Eixo de rotação = Z GLOBAL passando pelo vértice.
      ; Rotate3D é usado para não depender do UCS corrente.
      (vla-Rotate3D
        obj
        (vlax-3d-point origin)
        (vlax-3d-point
          (list (car origin) (cadr origin) (+ (caddr origin) 1.0))
        )
        ang
      )
      T
    )
    nil
  )
)

(defun A3S:help ()
  (princ "\n")
  (princ "\nARRAY3DSOLID - Array de 3D SOLID ao longo de uma 3DPOLY")
  (princ "\n----------------------------------------------------------------")
  (princ "\n1. Selecione o 3D SOLID modelo.")
  (princ "\n2. Informe o ponto-base do sólido.")
  (princ "\n3. Informe um segundo ponto indicando a direção longitudinal do sólido.")
  (princ "\n   Será usada SOMENTE a projeção XY dessa direção.")
  (princ "\n4. Selecione a 3DPOLY.")
  (princ "\n5. A rotina cria uma cópia em cada vértice.")
  (princ "\n")
  (princ "\nOrientação:")
  (princ "\n- Extremidades abertas: usa o segmento adjacente.")
  (princ "\n- Vértices internos: usa a bissetriz XY dos segmentos anterior/seguinte.")
  (princ "\n- 3DPOLY fechada: usa a bissetriz cíclica em todos os vértices.")
  (princ "\n- A face transversal do sólido fica ortogonal à direção da 3DPOLY em planta.")
  (princ "\n- O sólido gira SOMENTE no plano XY, em torno do Z global.")
  (princ "\n- A inclinação/declividade da 3DPOLY NÃO inclina o sólido.")
  (princ "\n")
  (princ "\nO sólido original é preservado.")
  (princ "\nComando de ajuda: ARRAY3DSOLIDHELP")
  (princ)
)

(defun c:ARRAY3DSOLIDHELP ()
  (A3S:help)
)

(defun c:ARRAY3DSOLID (/ *error* acad doc undoOpen
                         ss solEnt solObj baseU refU base ref srcDir
                         plSel pl pts closed n i p tan cp
                         okCount skipCount result)

  (vl-load-com)

  (setq acad     (vlax-get-acad-object)
        doc      (vla-get-ActiveDocument acad)
        undoOpen nil)

  (defun *error* (msg)
    (if undoOpen
      (progn
        (vl-catch-all-apply 'vla-EndUndoMark (list doc))
        (setq undoOpen nil)
      )
    )
    (if (and msg
             (/= msg "Function cancelled")
             (/= msg "quit / exit abort"))
      (princ (strcat "\n[ERRO] " msg))
    )
    (princ)
  )

  (princ "\nARRAY3DSOLID v1.1 - sólidos nos vértices | rotação somente XY.")

  (vla-StartUndoMark doc)
  (setq undoOpen T)

  (catch 'A3S-ABORT

    (setq ss (entsel "\nSelecione o 3D SOLID modelo: "))
    (if (not ss)
      (progn
        (princ "\nNenhum sólido selecionado.")
        (throw 'A3S-ABORT nil)
      )
    )

    (setq solEnt (car ss))
    (if (/= (cdr (assoc 0 (entget solEnt))) "3DSOLID")
      (progn
        (princ "\n[ERRO] O objeto selecionado não é um 3D SOLID.")
        (throw 'A3S-ABORT nil)
      )
    )

    (setq solObj (vlax-ename->vla-object solEnt))

    (setq baseU (getpoint "\nInforme o PONTO-BASE do sólido modelo: "))
    (if (not baseU)
      (progn
        (princ "\nOperação cancelada.")
        (throw 'A3S-ABORT nil)
      )
    )

    (setq refU
      (getpoint baseU
        "\nIndique um segundo ponto na DIREÇÃO LONGITUDINAL do sólido: "))
    (if (not refU)
      (progn
        (princ "\nOperação cancelada.")
        (throw 'A3S-ABORT nil)
      )
    )

    (setq base   (trans baseU 1 0)
          ref    (trans refU 1 0)
          srcDir (A3S:xy-unit (A3S:v- ref base)))

    (if (not srcDir)
      (progn
        (princ "\n[ERRO] A direção longitudinal informada não possui componente XY válida.")
        (throw 'A3S-ABORT nil)
      )
    )

    (setq plSel (entsel "\nSelecione a 3DPOLY de referência: "))
    (if (not plSel)
      (progn
        (princ "\nNenhuma 3DPOLY selecionada.")
        (throw 'A3S-ABORT nil)
      )
    )

    (setq pl (car plSel))
    (if (not (A3S:3dpoly-p pl))
      (progn
        (princ "\n[ERRO] O objeto selecionado não é uma 3DPOLY válida.")
        (throw 'A3S-ABORT nil)
      )
    )

    (setq pts    (A3S:get-verts pl)
          closed (A3S:closed-p pl)
          n      (length pts))

    (if (< n 2)
      (progn
        (princ "\n[ERRO] A 3DPOLY possui menos de 2 vértices válidos.")
        (throw 'A3S-ABORT nil)
      )
    )

    (princ
      (strcat
        "\n3DPOLY: "
        (itoa n)
        " vértice(s) | "
        (if closed "Fechada" "Aberta")
        "."
      )
    )

    (setq i         0
          okCount   0
          skipCount 0)

    (repeat n
      (setq p (nth i pts)
            tan
              (if closed
                (A3S:tangent-closed pts i)
                (A3S:tangent-open pts i)
              ))

      (if tan
        (progn
          (setq result
            (vl-catch-all-apply
              (function
                (lambda ()
                  (setq cp (vla-Copy solObj))

                  (vla-Move
                    cp
                    (vlax-3d-point base)
                    (vlax-3d-point p)
                  )

                  (if (A3S:rotate-xy-to-xy cp p srcDir tan)
                    T
                    (progn
                      (vl-catch-all-apply 'vla-Delete (list cp))
                      (setq cp nil)
                      nil
                    )
                  )
                )
              )
            )
          )

          (if (or (vl-catch-all-error-p result) (not result))
            (progn
              (if cp
                (vl-catch-all-apply 'vla-Delete (list cp))
              )
              (setq skipCount (1+ skipCount)
                    cp nil)
            )
            (setq okCount (1+ okCount)
                  cp nil)
          )
        )
        (setq skipCount (1+ skipCount))
      )

      (setq i (1+ i))
    )

    (princ
      (strcat
        "\nConcluído. "
        (itoa okCount)
        " sólido(s) criado(s)"
        (if (> skipCount 0)
          (strcat " | " (itoa skipCount) " vértice(s) ignorado(s).")
          "."
        )
      )
    )
    (princ "\nO 3D SOLID modelo original foi preservado.")
  )

  (if undoOpen
    (progn
      (vl-catch-all-apply 'vla-EndUndoMark (list doc))
      (setq undoOpen nil)
    )
  )

  (princ)
)

(princ "\nARRAY3DSOLID.LSP carregado. Comandos: ARRAY3DSOLID | ARRAY3DSOLIDHELP")
(princ)
