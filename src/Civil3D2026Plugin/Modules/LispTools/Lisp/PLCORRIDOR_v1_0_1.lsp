;;@TITULO  Auditoria de polilinhas para corredor
;;@CATEGORIA Polilinhas e geometria
;;@ICONE polilinha
;;@COMANDO PLCORRIDOR
;;@COMANDO PLANALISAR
;;@COMANDO PLINVERTER
;;@COMANDO PLORIGEM
;;@COMANDO PLDIAGCLEAR
;;@VERSAO  1.0.1
;;@DESC    Confere polilinhas antes de usar como Feature Line, target ou baseline: marca deflexao acima do limite,
;;@DESC    vertice duplicado, cruzamento e segmentos sobrepostos, sem alterar a geometria.
;;@DESC    Tambem inverte o sentido e redefine a origem (inclusive em polilinha fechada).
;;@USO     Digite PLCORRIDOR para o menu ou PLANALISAR para auditar
;;@USO     PLINVERTER e PLORIGEM editam o sentido e a origem
;;@USO     PLDIAGCLEAR apaga as marcas
;; ----------------------------------------------------------------
; ============================================================================
; PLCORRIDOR.LSP - v1.0.1
; Auditoria de polilinhas para uso posterior como Feature Lines / Targets /
; Baselines no Civil 3D.
;
; Comandos:
;   PLCORRIDOR  - menu principal
;   PLANALISAR  - audita uma ou varias polilinhas
;   PLINVERTER  - inverte o sentido de uma ou varias polilinhas
;   PLORIGEM    - redefine a origem:
;                 * aberta: escolha uma das extremidades; inverte se preciso
;                 * LWPOLYLINE fechada: permite escolher qualquer vertice
;   PLDIAGCLEAR - apaga as marcacoes de diagnostico
;
; Diagnosticos:
;   VERMELHO  : deflexao maior que o limite
;   AMARELO   : vertice/ponto duplicado
;   MAGENTA   : cruzamento/intersecao indevida
;   CIANO     : segmentos retos sobrepostos
;
; Filosofia:
; - analise nao altera a geometria;
; - usa copias explodidas temporarias para testar segmentos;
; - edicoes ficam em grupo unico de UNDO;
; - trabalha em XY para a auditoria planimetrica;
; - suporta LWPOLYLINE e POLYLINE 2D/3D na auditoria e inversao.
; ============================================================================

(vl-load-com)

(setq *PLC-TOL* 0.010)
(setq *PLC-ANG* 30.0)
(setq *PLC-RAD* 0.20)
(setq *PLC-DIAG-LAYER* "PL_CORRIDOR_DIAG")

; ------------------------------ basico ---------------------------------------

(defun plc:abs (x) (if (< x 0.0) (- x) x))

(defun plc:pt3 (p)
  (list
    (if p (car p) 0.0)
    (if p (cadr p) 0.0)
    (if (and p (caddr p)) (caddr p) 0.0)
  )
)

(defun plc:pt2 (p)
  (list (car p) (cadr p) 0.0)
)

(defun plc:dist2d (a b)
  (distance (plc:pt2 a) (plc:pt2 b))
)

(defun plc:etype (e)
  (if e (cdr (assoc 0 (entget e))))
)

(defun plc:poly-p (e / typ flags)
  (if e
    (progn
      (setq typ (plc:etype e))
      (cond
        ((= typ "LWPOLYLINE") T)
        ((= typ "POLYLINE")
          (setq flags (cdr (assoc 70 (entget e))))
          ; exclui polygon mesh e polyface mesh
          (and
            (= 0 (logand flags 16))
            (= 0 (logand flags 64))
          )
        )
        (T nil)
      )
    )
  )
)

(defun plc:closed-p (e / r)
  (setq r (vl-catch-all-apply 'vlax-curve-isClosed (list e)))
  (if (vl-catch-all-error-p r) nil r)
)

(defun plc:safe-delete (obj)
  (if obj
    (vl-catch-all-apply 'vla-Delete (list obj))
  )
)

(defun plc:safe-variant-list (x / y r)
  (cond
    ((null x) nil)
    ((= (type x) 'VARIANT)
      (setq y (vl-catch-all-apply 'vlax-variant-value (list x)))
      (if (vl-catch-all-error-p y)
        nil
        (plc:safe-variant-list y)
      )
    )
    ((= (type x) 'SAFEARRAY)
      (setq r (vl-catch-all-apply 'vlax-safearray->list (list x)))
      (if (vl-catch-all-error-p r) nil r)
    )
    ((listp x) x)
    (T nil)
  )
)

(defun plc:triples (lst / out)
  (setq out nil)
  (while (and lst (cadr lst) (caddr lst))
    (setq out
      (cons
        (list (car lst) (cadr lst) (caddr lst))
        out
      )
    )
    (setq lst (cdddr lst))
  )
  (reverse out)
)

(defun plc:fmt (x)
  (rtos x 2 3)
)

; ------------------------------ layer/mark ----------------------------------

(defun plc:ensure-diag-layer (/ doc lays lay)
  (setq doc (vla-get-ActiveDocument (vlax-get-acad-object))
        lays (vla-get-Layers doc))
  (if (tblsearch "LAYER" *PLC-DIAG-LAYER*)
    (setq lay (vla-Item lays *PLC-DIAG-LAYER*))
    (setq lay (vla-Add lays *PLC-DIAG-LAYER*))
  )
  (vl-catch-all-apply 'vla-put-LayerOn (list lay :vlax-true))
  (vl-catch-all-apply 'vla-put-Freeze (list lay :vlax-false))
  (vl-catch-all-apply 'vla-put-Lock (list lay :vlax-false))
  (if (vlax-property-available-p lay 'Plottable T)
    (vl-catch-all-apply 'vla-put-Plottable (list lay :vlax-false))
  )
  lay
)

(defun plc:space (/ doc)
  (setq doc (vla-get-ActiveDocument (vlax-get-acad-object)))
  (if (= (getvar "CVPORT") 1)
    (vla-get-PaperSpace doc)
    (vla-get-ModelSpace doc)
  )
)

(defun plc:mark (pt color tag / sp c t1 h ptxt)
  (plc:ensure-diag-layer)
  (setq sp (plc:space))
  (setq c
    (vl-catch-all-apply
      'vla-AddCircle
      (list sp (vlax-3d-point (plc:pt3 pt)) *PLC-RAD*)
    )
  )
  (if (not (vl-catch-all-error-p c))
    (progn
      (vla-put-Layer c *PLC-DIAG-LAYER*)
      (vla-put-Color c color)
      (vla-put-Lineweight c 25)
    )
  )
  (setq h (max 0.10 (* *PLC-RAD* 0.75)))
  (setq ptxt
    (list
      (+ (car pt) (* *PLC-RAD* 1.15))
      (+ (cadr pt) (* *PLC-RAD* 1.15))
      (if (caddr pt) (caddr pt) 0.0)
    )
  )
  (setq t1
    (vl-catch-all-apply
      'vla-AddText
      (list sp tag (vlax-3d-point ptxt) h)
    )
  )
  (if (not (vl-catch-all-error-p t1))
    (progn
      (vla-put-Layer t1 *PLC-DIAG-LAYER*)
      (vla-put-Color t1 color)
      (vla-put-Lineweight t1 25)
    )
  )
)

(defun c:PLDIAGCLEAR (/ ss)
  (if (setq ss (ssget "_X" (list (cons 8 *PLC-DIAG-LAYER*))))
    (progn
      (command-s "_.ERASE" ss "")
      (princ "\nMarcacoes de diagnostico removidas.")
    )
    (princ "\nNenhuma marcacao de diagnostico encontrada.")
  )
  (princ)
)

; ----------------------------- bbox/intersecao -------------------------------

(defun plc:bbox (obj / mn mx r)
  (setq r
    (vl-catch-all-apply
      '(lambda ()
         (vla-GetBoundingBox obj 'mn 'mx)
         (list
           (vlax-safearray->list mn)
           (vlax-safearray->list mx)
         )
       )
      nil
    )
  )
  (if (vl-catch-all-error-p r) nil r)
)

(defun plc:bbox-overlap-p (b1 b2 tol / a1 a2 c1 c2)
  (if (and b1 b2)
    (progn
      (setq a1 (car b1)
            a2 (cadr b1)
            c1 (car b2)
            c2 (cadr b2))
      (and
        (<= (- (car a1) tol) (+ (car c2) tol))
        (<= (- (car c1) tol) (+ (car a2) tol))
        (<= (- (cadr a1) tol) (+ (cadr c2) tol))
        (<= (- (cadr c1) tol) (+ (cadr a2) tol))
      )
    )
    nil
  )
)

(defun plc:intersections (a b / r)
  (setq r
    (vl-catch-all-apply
      'vla-IntersectWith
      (list a b 0)
    )
  )
  (if (vl-catch-all-error-p r)
    nil
    (plc:triples (plc:safe-variant-list r))
  )
)

(defun plc:endpoint-p (obj p tol / s e)
  (setq s (vl-catch-all-apply 'vlax-curve-getStartPoint (list obj))
        e (vl-catch-all-apply 'vlax-curve-getEndPoint   (list obj)))
  (or
    (and (not (vl-catch-all-error-p s))
         (<= (plc:dist2d s p) tol))
    (and (not (vl-catch-all-error-p e))
         (<= (plc:dist2d e p) tol))
  )
)

(defun plc:line-overlap-point (a b tol / p1 p2 q1 q2 vx vy wx wy cross len2 t1 t2 lo hi tm)
  (if (and
        (= (vla-get-ObjectName a) "AcDbLine")
        (= (vla-get-ObjectName b) "AcDbLine")
      )
    (progn
      (setq p1 (plc:pt3 (vlax-curve-getStartPoint a))
            p2 (plc:pt3 (vlax-curve-getEndPoint a))
            q1 (plc:pt3 (vlax-curve-getStartPoint b))
            q2 (plc:pt3 (vlax-curve-getEndPoint b))
            vx (- (car p2) (car p1))
            vy (- (cadr p2) (cadr p1))
            wx (- (car q1) (car p1))
            wy (- (cadr q1) (cadr p1))
            len2 (+ (* vx vx) (* vy vy)))
      (if (> len2 1.0e-16)
        (progn
          (setq cross (abs (- (* vx wy) (* vy wx))))
          (if (<= cross (* tol (sqrt len2)))
            (progn
              (setq t1 (/ (+ (* (- (car q1) (car p1)) vx)
                             (* (- (cadr q1) (cadr p1)) vy))
                          len2))
              (setq t2 (/ (+ (* (- (car q2) (car p1)) vx)
                             (* (- (cadr q2) (cadr p1)) vy))
                          len2))
              (setq lo (max 0.0 (min t1 t2))
                    hi (min 1.0 (max t1 t2)))
              (if (> (- hi lo) (/ tol (max (sqrt len2) 1.0e-12)))
                (progn
                  (setq tm (/ (+ lo hi) 2.0))
                  (list
                    (+ (car p1) (* tm vx))
                    (+ (cadr p1) (* tm vy))
                    (/ (+ (caddr p1) (caddr p2)) 2.0)
                  )
                )
                nil
              )
            )
            nil
          )
        )
        nil
      )
    )
    nil
  )
)

; -------------------------- vertices/segmentos -------------------------------

(defun plc:get-vertices (e / sp ep closed i lim p out)
  ; vlax-curve integer parameters correspond to polyline vertices.
  (setq sp (vl-catch-all-apply 'vlax-curve-getStartParam (list e))
        ep (vl-catch-all-apply 'vlax-curve-getEndParam   (list e))
        closed (plc:closed-p e)
        out nil)
  (if (or (vl-catch-all-error-p sp) (vl-catch-all-error-p ep))
    nil
    (progn
      (setq i (fix sp))
      (setq lim (fix ep))
      (if closed
        (setq lim (1- lim))
      )
      (while (<= i lim)
        (setq p
          (vl-catch-all-apply
            'vlax-curve-getPointAtParam
            (list e i)
          )
        )
        (if (not (vl-catch-all-error-p p))
          (setq out (cons (plc:pt3 p) out))
        )
        (setq i (1+ i))
      )
      (reverse out)
    )
  )
)

(defun plc:explode-copy (obj / cp r)
  (setq cp (vl-catch-all-apply 'vla-Copy (list obj)))
  (if (vl-catch-all-error-p cp)
    nil
    (progn
      (setq r (vl-catch-all-apply 'vla-Explode (list cp)))
      (plc:safe-delete cp)
      (if (vl-catch-all-error-p r)
        nil
        (plc:safe-variant-list r)
      )
    )
  )
)

(defun plc:seg-mid (obj / sp ep d1 d2 p)
  (setq sp (vl-catch-all-apply 'vlax-curve-getStartParam (list obj))
        ep (vl-catch-all-apply 'vlax-curve-getEndParam   (list obj)))
  (if (or (vl-catch-all-error-p sp) (vl-catch-all-error-p ep))
    nil
    (progn
      (setq p
        (vl-catch-all-apply
          'vlax-curve-getPointAtParam
          (list obj (/ (+ sp ep) 2.0))
        )
      )
      (if (vl-catch-all-error-p p) nil (plc:pt3 p))
    )
  )
)

(defun plc:seg-vector-start (obj / s e len p0 p1 d)
  (setq s (vl-catch-all-apply 'vlax-curve-getStartPoint (list obj))
        e (vl-catch-all-apply 'vlax-curve-getEndPoint   (list obj)))
  (if (or (vl-catch-all-error-p s) (vl-catch-all-error-p e))
    nil
    (progn
      (setq len
        (vl-catch-all-apply
          'vlax-curve-getDistAtParam
          (list obj (vlax-curve-getEndParam obj))
        )
      )
      (if (or (vl-catch-all-error-p len) (<= len 1.0e-12))
        nil
        (progn
          (setq d (min (* len 0.02) (max (* *PLC-TOL* 5.0) 0.001)))
          (setq p0 (plc:pt3 s))
          (setq p1
            (vl-catch-all-apply
              'vlax-curve-getPointAtDist
              (list obj d)
            )
          )
          (if (vl-catch-all-error-p p1)
            nil
            (list (- (car p1) (car p0)) (- (cadr p1) (cadr p0)))
          )
        )
      )
    )
  )
)

(defun plc:seg-vector-end (obj / s e len p0 p1 d)
  (setq s (vl-catch-all-apply 'vlax-curve-getStartPoint (list obj))
        e (vl-catch-all-apply 'vlax-curve-getEndPoint   (list obj)))
  (if (or (vl-catch-all-error-p s) (vl-catch-all-error-p e))
    nil
    (progn
      (setq len
        (vl-catch-all-apply
          'vlax-curve-getDistAtParam
          (list obj (vlax-curve-getEndParam obj))
        )
      )
      (if (or (vl-catch-all-error-p len) (<= len 1.0e-12))
        nil
        (progn
          (setq d (min (* len 0.02) (max (* *PLC-TOL* 5.0) 0.001)))
          (setq p1 (plc:pt3 e))
          (setq p0
            (vl-catch-all-apply
              'vlax-curve-getPointAtDist
              (list obj (max 0.0 (- len d)))
            )
          )
          (if (vl-catch-all-error-p p0)
            nil
            (list (- (car p1) (car p0)) (- (cadr p1) (cadr p0)))
          )
        )
      )
    )
  )
)

(defun plc:angle-between (v1 v2 / n1 n2 dot c s ang)
  (setq n1 (sqrt (+ (* (car v1) (car v1)) (* (cadr v1) (cadr v1))))
        n2 (sqrt (+ (* (car v2) (car v2)) (* (cadr v2) (cadr v2)))))
  (if (or (<= n1 1.0e-12) (<= n2 1.0e-12))
    0.0
    (progn
      (setq dot (+ (* (car v1) (car v2)) (* (cadr v1) (cadr v2))))
      (setq c (/ dot (* n1 n2)))
      (setq c (max -1.0 (min 1.0 c)))
      ; AutoLISP nao possui ACOS em todas as versoes.
      ; acos(c) = atan(sqrt(1-c^2), c)
      (setq s (sqrt (max 0.0 (- 1.0 (* c c)))))
      (setq ang (atan s c))
      (if (< ang 0.0)
        (setq ang (+ ang (* 2.0 pi)))
      )
      (* 180.0 (/ ang pi))
    )
  )
)

(defun plc:make-segrecs (e pid / obj segs out idx s n closed)
  (setq obj (vlax-ename->vla-object e))
  (setq segs (plc:explode-copy obj))
  (setq out nil idx 0 n (length segs) closed (plc:closed-p e))
  (foreach s segs
    (setq out
      (cons
        (list
          (cons 'obj s)
          (cons 'bbox (plc:bbox s))
          (cons 'idx idx)
          (cons 'pid pid)
          (cons 'n n)
          (cons 'closed closed)
          (cons 'mid (plc:seg-mid s))
        )
        out
      )
    )
    (setq idx (1+ idx))
  )
  (reverse out)
)

(defun plc:av (key al)
  (cdr (assoc key al))
)

(defun plc:adjacent-recs-p (a b / ia ib n closed)
  (setq ia (plc:av 'idx a)
        ib (plc:av 'idx b)
        n (plc:av 'n a)
        closed (plc:av 'closed a))
  (or
    (= (abs (- ia ib)) 1)
    (and closed
         (or
           (and (= ia 0) (= ib (1- n)))
           (and (= ib 0) (= ia (1- n)))
         )
    )
  )
)

(defun plc:delete-segrecs (recs / r)
  (foreach r recs
    (plc:safe-delete (plc:av 'obj r))
  )
)

; ------------------------------- auditoria ----------------------------------

(defun plc:audit-duplicates (e / pts n i j p q closed cnt)
  (setq pts (plc:get-vertices e)
        n (length pts)
        i 0
        cnt 0
        closed (plc:closed-p e))
  (while (< i n)
    (setq p (nth i pts)
          j (1+ i))
    (while (< j n)
      (setq q (nth j pts))
      (if (<= (plc:dist2d p q) *PLC-TOL*)
        (progn
          ; marca repeticoes consecutivas e nao consecutivas.
          ; para fechada, primeiro/ultimo da lista nao sao repetidos artificialmente.
          (plc:mark p 2 "DUP")
          (setq cnt (1+ cnt))
          (setq j n)
        )
      )
      (setq j (1+ j))
    )
    (setq i (1+ i))
  )
  cnt
)

(defun plc:audit-deflections (recs / n i j a b oa ob v1 v2 ang p cnt closed)
  (setq n (length recs)
        cnt 0)
  (if (> n 1)
    (progn
      (setq closed (plc:av 'closed (car recs)))
      (setq i 0)
      (while (< i n)
        (setq j (1+ i))
        (if (= j n)
          (if closed (setq j 0) (setq j nil))
        )
        (if j
          (progn
            (setq a (nth i recs)
                  b (nth j recs)
                  oa (plc:av 'obj a)
                  ob (plc:av 'obj b)
                  v1 (plc:seg-vector-end oa)
                  v2 (plc:seg-vector-start ob))
            (if (and v1 v2)
              (progn
                (setq ang (plc:angle-between v1 v2))
                (if (> ang *PLC-ANG*)
                  (progn
                    (setq p
                      (vl-catch-all-apply
                        'vlax-curve-getEndPoint
                        (list oa)
                      )
                    )
                    (if (not (vl-catch-all-error-p p))
                      (plc:mark p 1 (strcat "DEF " (rtos ang 2 1)))
                    )
                    (setq cnt (1+ cnt))
                  )
                )
              )
            )
          )
        )
        (setq i (1+ i))
      )
    )
  )
  cnt
)

(defun plc:audit-pairs (allrecs / i j a b oa ob same adjacent ov ips p cntx cnto)
  (setq i 0 cntx 0 cnto 0)
  (while (< i (length allrecs))
    (setq a (nth i allrecs)
          j (1+ i))
    (while (< j (length allrecs))
      (setq b (nth j allrecs))
      (if (plc:bbox-overlap-p (plc:av 'bbox a) (plc:av 'bbox b) *PLC-TOL*)
        (progn
          (setq oa (plc:av 'obj a)
                ob (plc:av 'obj b)
                same (= (plc:av 'pid a) (plc:av 'pid b))
                adjacent (and same (plc:adjacent-recs-p a b))
                ov (plc:line-overlap-point oa ob *PLC-TOL*))
          (if ov
            (progn
              (plc:mark ov 4 "SOB")
              (setq cnto (1+ cnto))
            )
            (if (not adjacent)
              (progn
                (setq ips (plc:intersections oa ob))
                (foreach p ips
                  ; em entidades diferentes, encontro ponta-ponta e permitido.
                  ; ponta-interior ou interior-interior e marcado.
                  (if (not
                        (and
                          (not same)
                          (plc:endpoint-p oa p *PLC-TOL*)
                          (plc:endpoint-p ob p *PLC-TOL*)
                        )
                      )
                    (progn
                      (plc:mark p 6 "X")
                      (setq cntx (1+ cntx))
                    )
                  )
                )
              )
            )
          )
        )
      )
      (setq j (1+ j))
    )
    (setq i (1+ i))
  )
  (list cntx cnto)
)

(defun plc:get-selection (/ ss)
  (princ "\nSelecione LWPOLYLINE / POLYLINE 2D ou 3D: ")
  (ssget '((0 . "LWPOLYLINE,POLYLINE")))
)

(defun c:PLANALISAR (/ *error* doc ss i e ents pid recs allrecs dupc defc pairc xc ovc val)
  (setq doc (vla-get-ActiveDocument (vlax-get-acad-object)))

  (defun *error* (msg)
    (if allrecs (plc:delete-segrecs allrecs))
    (vl-catch-all-apply 'vla-EndUndoMark (list doc))
    (if (and msg (/= msg "Function cancelled"))
      (princ (strcat "\nErro: " msg))
    )
    (princ)
  )

  (initget 6)
  (setq val
    (getreal
      (strcat "\nTolerancia XY em unidades do desenho <"
              (rtos *PLC-TOL* 2 3) ">: ")
    )
  )
  (if val (setq *PLC-TOL* val))

  (initget 6)
  (setq val
    (getreal
      (strcat "\nDeflexao maxima permitida em graus <"
              (rtos *PLC-ANG* 2 1) ">: ")
    )
  )
  (if val (setq *PLC-ANG* val))

  (initget 6)
  (setq val
    (getreal
      (strcat "\nRaio das marcacoes <"
              (rtos *PLC-RAD* 2 2) ">: ")
    )
  )
  (if val (setq *PLC-RAD* val))

  (setq ss (plc:get-selection))

  (if ss
    (progn
      (vla-StartUndoMark doc)
      (plc:ensure-diag-layer)
      (setq i 0
            pid 0
            ents nil
            allrecs nil
            dupc 0
            defc 0)

      (while (< i (sslength ss))
        (setq e (ssname ss i))
        (if (plc:poly-p e)
          (progn
            (setq ents (cons e ents))
            (setq dupc (+ dupc (plc:audit-duplicates e)))
            (setq recs (plc:make-segrecs e pid))
            (setq defc (+ defc (plc:audit-deflections recs)))
            (setq allrecs (append allrecs recs))
            (setq pid (1+ pid))
          )
        )
        (setq i (1+ i))
      )

      (setq pairc (plc:audit-pairs allrecs)
            xc (car pairc)
            ovc (cadr pairc))

      (plc:delete-segrecs allrecs)
      (setq allrecs nil)

      (vla-EndUndoMark doc)

      (princ "\n")
      (princ "\n================ PLCORRIDOR - RESULTADO ================")
      (princ (strcat "\nPolilinhas analisadas: " (itoa (length ents))))
      (princ (strcat "\nPontos/vertices duplicados: " (itoa dupc)))
      (princ (strcat "\nDeflexoes > " (rtos *PLC-ANG* 2 1) " graus: " (itoa defc)))
      (princ (strcat "\nCruzamentos/intersecoes indevidas: " (itoa xc)))
      (princ (strcat "\nSegmentos retos sobrepostos: " (itoa ovc)))
      (if (= (+ dupc defc xc ovc) 0)
        (princ "\nSTATUS: nenhuma irregularidade detectada pelos testes desta versao.")
        (princ (strcat "\nSTATUS: verifique as marcacoes no layer " *PLC-DIAG-LAYER* "."))
      )
      (princ "\n=========================================================")
    )
    (princ "\nNenhuma polilinha selecionada.")
  )
  (princ)
)

; ----------------------------- inverter -------------------------------------

(defun c:PLINVERTER (/ *error* doc ss oldecho ret)
  (setq doc (vla-get-ActiveDocument (vlax-get-acad-object))
        oldecho (getvar "CMDECHO"))

  (defun *error* (msg)
    (setvar "CMDECHO" oldecho)
    (vl-catch-all-apply 'vla-EndUndoMark (list doc))
    (if (and msg (/= msg "Function cancelled"))
      (princ (strcat "\nErro: " msg))
    )
    (princ)
  )

  (setq ss (plc:get-selection))
  (if ss
    (progn
      (vla-StartUndoMark doc)
      (setvar "CMDECHO" 0)
      (setq ret
        (vl-catch-all-apply
          '(lambda () (command-s "_.REVERSE" ss ""))
          nil
        )
      )
      (setvar "CMDECHO" oldecho)
      (vla-EndUndoMark doc)
      (if (vl-catch-all-error-p ret)
        (princ "\nNao foi possivel inverter uma ou mais polilinhas.")
        (princ "\nSentido invertido.")
      )
    )
  )
  (princ)
)

; ----------------------- origem de LWPOLYLINE fechada ------------------------

(defun plc:lw-vertex-records (e / ed cur out p bul sw ew)
  ; Retorna lista: ((point bulge startWidth endWidth) ...)
  ; Larguras nao existentes ficam nil.
  (setq ed (entget e)
        out nil
        cur nil)
  (foreach x ed
    (cond
      ((= (car x) 10)
        (if cur (setq out (cons cur out)))
        (setq cur (list (cdr x) 0.0 nil nil))
      )
      ((and cur (= (car x) 42))
        (setq cur (list (nth 0 cur) (cdr x) (nth 2 cur) (nth 3 cur)))
      )
      ((and cur (= (car x) 40))
        (setq cur (list (nth 0 cur) (nth 1 cur) (cdr x) (nth 3 cur)))
      )
      ((and cur (= (car x) 41))
        (setq cur (list (nth 0 cur) (nth 1 cur) (nth 2 cur) (cdr x)))
      )
    )
  )
  (if cur (setq out (cons cur out)))
  (reverse out)
)

(defun plc:rotate-list (lst k / a b)
  (setq a nil b nil)
  (repeat k
    (setq a (append a (list (car lst))))
    (setq lst (cdr lst))
  )
  (append lst a)
)

(defun plc:nearest-index-2d (pts pick / i best bestd d)
  (setq i 0 best 0 bestd nil)
  (foreach p pts
    (setq d (plc:dist2d p pick))
    (if (or (null bestd) (< d bestd))
      (setq bestd d best i)
    )
    (setq i (1+ i))
  )
  best
)

(defun plc:strip-lw-vertex-codes (ed / out x)
  (setq out nil)
  (foreach x ed
    (if (not (member (car x) '(10 40 41 42 91)))
      (setq out (cons x out))
    )
  )
  (reverse out)
)

(defun plc:set-lw-origin (e idx / ed recs rot base out r pt bul sw ew)
  (setq ed (entget e)
        recs (plc:lw-vertex-records e)
        rot (plc:rotate-list recs idx)
        base (plc:strip-lw-vertex-codes ed)
        out nil)

  ; Reconstroi a lista DXF inserindo os vertices logo apos o codigo 90.
  (foreach x base
    (setq out (append out (list x)))
    (if (= (car x) 90)
      (foreach r rot
        (setq pt (nth 0 r)
              bul (nth 1 r)
              sw (nth 2 r)
              ew (nth 3 r))
        (setq out (append out (list (cons 10 pt))))
        (if sw (setq out (append out (list (cons 40 sw)))))
        (if ew (setq out (append out (list (cons 41 ew)))))
        (if (/= bul 0.0)
          (setq out (append out (list (cons 42 bul))))
        )
      )
    )
  )

  (if (entmod out)
    (progn
      (entupd e)
      T
    )
    nil
  )
)

(defun c:PLORIGEM (/ *error* doc sel e typ closed pick pts idx sp ep ds de ret)
  (setq doc (vla-get-ActiveDocument (vlax-get-acad-object)))

  (defun *error* (msg)
    (vl-catch-all-apply 'vla-EndUndoMark (list doc))
    (if (and msg (/= msg "Function cancelled"))
      (princ (strcat "\nErro: " msg))
    )
    (princ)
  )

  (princ "\nSelecione uma polilinha: ")
  (setq sel (entsel))
  (if sel
    (progn
      (setq e (car sel))
      (if (not (plc:poly-p e))
        (princ "\nO objeto selecionado nao e uma polilinha suportada.")
        (progn
          (setq typ (plc:etype e)
                closed (plc:closed-p e))
          (setq pick (getpoint "\nClique proximo ao vertice que deve ser a ORIGEM: "))
          (if pick
            (progn
              (vla-StartUndoMark doc)
              (if closed
                (if (= typ "LWPOLYLINE")
                  (progn
                    (setq pts (mapcar 'car (plc:lw-vertex-records e)))
                    (setq idx (plc:nearest-index-2d pts pick))
                    (if (plc:set-lw-origin e idx)
                      (princ (strcat "\nOrigem alterada para o vertice " (itoa (1+ idx)) "."))
                      (princ "\nNao foi possivel alterar a origem.")
                    )
                  )
                  (princ
                    "\nNesta versao, origem arbitraria em polilinha FECHADA esta disponivel para LWPOLYLINE. Use PEDIT/CONVERTPOLY se necessario."
                  )
                )
                (progn
                  ; Em polilinha aberta, a origem so pode ser uma das pontas
                  ; sem alterar a geometria. Se clicou mais perto do fim, inverte.
                  (setq sp (vlax-curve-getStartPoint e)
                        ep (vlax-curve-getEndPoint e)
                        ds (plc:dist2d pick sp)
                        de (plc:dist2d pick ep))
                  (if (< de ds)
                    (progn
                      (setq ret
                        (vl-catch-all-apply
                          '(lambda ()
                             (command-s "_.REVERSE" e "")
                           )
                          nil
                        )
                      )
                      (if (vl-catch-all-error-p ret)
                        (princ "\nNao foi possivel inverter a polilinha.")
                        (princ "\nOrigem alterada para a antiga extremidade final.")
                      )
                    )
                    (princ "\nA extremidade inicial atual ja e a origem escolhida.")
                  )
                )
              )
              (vla-EndUndoMark doc)
            )
          )
        )
      )
    )
  )
  (princ)
)

; ------------------------------ menu -----------------------------------------

(defun c:PLCORRIDOR (/ op)
  (initget "Analisar Inverter Origem Limpar")
  (setq op
    (getkword
      "\nPLCORRIDOR [Analisar/Inverter/Origem/Limpar] <Analisar>: "
    )
  )
  (if (null op) (setq op "Analisar"))
  (cond
    ((= op "Analisar") (c:PLANALISAR))
    ((= op "Inverter") (c:PLINVERTER))
    ((= op "Origem") (c:PLORIGEM))
    ((= op "Limpar") (c:PLDIAGCLEAR))
  )
  (princ)
)

(princ "\nPLCORRIDOR v1.0.1 carregada.")
(princ "\nComandos: PLCORRIDOR, PLANALISAR, PLINVERTER, PLORIGEM, PLDIAGCLEAR.")
(princ)
