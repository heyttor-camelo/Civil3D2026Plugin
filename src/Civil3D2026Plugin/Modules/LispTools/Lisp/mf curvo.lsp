;;@TITULO  Separar curvas de polilinha por raio
;;@CATEGORIA Vias e meio-fio
;;@COMANDO SEPARA_CURVAS_RAIO
;;@DESC    Quebra cada polilinha selecionada em trechos retos, curvos com raio acima do limite e curvos com
;;@DESC    raio ate o limite (estes em cor 6). Mostra quantidade e comprimento de cada grupo, util para
;;@DESC    quantificar meio-fio curvo. A polilinha original e substituida pelos trechos.
;;@USO     Digite SEPARA_CURVAS_RAIO
;;@USO     Informe o raio limite
;;@USO     Selecione as polilinhas
;; ----------------------------------------------------------------
(vl-load-com)

(defun _pts-from-coords (lst / out)
  (setq out '())
  (while lst
    (setq out (cons (list (car lst) (cadr lst)) out))
    (setq lst (cddr lst))
  )
  (reverse out)
)

(defun _lwpoly-data (obj / coords pts bulges i n closed)
  (setq coords  (vlax-safearray->list (vlax-variant-value (vla-get-Coordinates obj))))
  (setq pts     (_pts-from-coords coords))
  (setq n       (length pts))
  (setq closed  (= :vlax-true (vla-get-Closed obj)))
  (setq bulges '()
        i      0)
  (repeat n
    (setq bulges (cons (vla-GetBulge obj i) bulges))
    (setq i (1+ i))
  )
  (list pts (reverse bulges) closed)
)

(defun _style-common (ent / elst out)
  (setq elst (entget ent))
  (setq out '())

  (if (assoc 8 elst)   (setq out (append out (list (assoc 8 elst)))))   ;; layer
  (if (assoc 6 elst)   (setq out (append out (list (assoc 6 elst)))))   ;; linetype
  (if (assoc 48 elst)  (setq out (append out (list (assoc 48 elst)))))  ;; ltscale
  (if (assoc 39 elst)  (setq out (append out (list (assoc 39 elst)))))  ;; thickness
  (if (assoc 370 elst) (setq out (append out (list (assoc 370 elst))))) ;; lineweight
  (if (assoc 210 elst) (setq out (append out (list (assoc 210 elst))))) ;; normal
  (if (assoc 38 elst)  (setq out (append out (list (assoc 38 elst)))))  ;; elevation

  out
)

(defun _style-lwpoly (ent / elst out)
  (setq elst (entget ent))
  (setq out (_style-common ent))

  ;; válido só para lwpolyline
  (if (assoc 43 elst) (setq out (append out (list (assoc 43 elst))))) ;; global width

  out
)

(defun _style-with-color (style color / out)
  (setq out '())
  (foreach x style
    (if (/= (car x) 62)
      (setq out (append out (list x)))
    )
  )
  (if color
    (setq out (append out (list (cons 62 color))))
  )
  out
)

(defun _arc-radius (p1 p2 b / a)
  (setq a (* 2.0 (atan b)))
  (abs (/ (distance p1 p2) 2.0 (sin a)))
)

(defun _arc-length (p1 p2 b / theta r)
  (setq theta (* 4.0 (atan b)))
  (setq r (_arc-radius p1 p2 b))
  (* r (abs theta))
)

(defun _make-lwpolyline (pts bulges style closedFlag / n data i)
  (setq n (length pts))
  (if (>= n 2)
    (progn
      (setq data
        (list
          '(0 . "LWPOLYLINE")
          '(100 . "AcDbEntity")
        )
      )

      (setq data (append data style))

      (setq data
        (append data
          (list
            '(100 . "AcDbPolyline")
            (cons 90 n)
            (cons 70 (if closedFlag 1 0))
          )
        )
      )

      (setq i 0)
      (while (< i n)
        (setq data (append data (list (cons 10 (nth i pts)))))
        (if (< i (length bulges))
          (setq data (append data (list (cons 42 (nth i bulges)))))
        )
        (setq i (1+ i))
      )

      (entmakex data)
    )
  )
)

(defun _flush-run (runPts runBulges style color / sty)
  (if (and runPts (>= (length runPts) 2))
    (progn
      (setq sty (_style-with-color style color))
      (_make-lwpolyline runPts runBulges sty nil)
    )
  )
)

(defun _classify-seg (p1 p2 b raio)
  (if (equal b 0.0 1e-12)
    'RETO
    (if (> (_arc-radius p1 p2 b) raio)
      'CURVA_MANTER
      'CURVA_SEPARAR
    )
  )
)

(defun c:SEPARA_CURVAS_RAIO
       (/ raio ss i ent obj dat pts bulges closed
          n segCount styleLW idx p1 p2 b r tipo
          lineCnt lineLen curveKeepCnt curveKeepLen
          curveSplitCnt curveSplitLen
          classes ord firstRemoved
          runPts runBulges runType curType
          pStart pEnd msg)

  (vl-load-com)

  (setq raio (getreal "\nInforme o raio limite: "))
  (if (or (null raio) (<= raio 0.0))
    (progn
      (prompt "\nRaio invalido.")
      (princ)
    )
    (progn
      (setq ss (ssget '((0 . "LWPOLYLINE"))))

      (if (null ss)
        (prompt "\nNenhuma LWPOLYLINE selecionada.")
        (progn
          (setq lineCnt 0
                lineLen 0.0
                curveKeepCnt 0
                curveKeepLen 0.0
                curveSplitCnt 0
                curveSplitLen 0.0
                i 0)

          (repeat (sslength ss)
            (setq ent (ssname ss i))
            (setq obj (vlax-ename->vla-object ent))
            (setq styleLW (_style-lwpoly ent))

            (setq dat    (_lwpoly-data obj))
            (setq pts    (car dat))
            (setq bulges (cadr dat))
            (setq closed (caddr dat))
            (setq n      (length pts))

            (if closed
              (setq segCount n)
              (setq segCount (1- n))
            )

            ;; classificar segmentos e acumular quantitativos
            (setq classes '()
                  idx 0)

            (repeat segCount
              (setq p1 (nth idx pts))
              (setq p2 (if (= idx (1- n)) (car pts) (nth (1+ idx) pts)))
              (setq b  (nth idx bulges))
              (setq tipo (_classify-seg p1 p2 b raio))

              (setq classes (append classes (list tipo)))

              (cond
                ((eq tipo 'RETO)
                  (setq lineCnt (1+ lineCnt))
                  (setq lineLen (+ lineLen (distance p1 p2)))
                )
                ((eq tipo 'CURVA_MANTER)
                  (setq curveKeepCnt (1+ curveKeepCnt))
                  (setq curveKeepLen (+ curveKeepLen (_arc-length p1 p2 b)))
                )
                ((eq tipo 'CURVA_SEPARAR)
                  (setq curveSplitCnt (1+ curveSplitCnt))
                  (setq curveSplitLen (+ curveSplitLen (_arc-length p1 p2 b)))
                )
              )

              (setq idx (1+ idx))
            )

            ;; se não houver nada para separar, mantém original
            (if (member 'CURVA_SEPARAR classes)
              (progn
                ;; ordem de varredura para polilinha fechada
                (if closed
                  (progn
                    (setq firstRemoved 0)
                    (while (and (< firstRemoved segCount)
                                (not (eq (nth firstRemoved classes) 'CURVA_SEPARAR)))
                      (setq firstRemoved (1+ firstRemoved))
                    )

                    (setq ord '()
                          idx 1)
                    (repeat segCount
                      (setq ord (append ord (list (rem (+ firstRemoved idx) segCount))))
                      (setq idx (1+ idx))
                    )
                  )
                  (progn
                    (setq ord '()
                          idx 0)
                    (repeat segCount
                      (setq ord (append ord (list idx)))
                      (setq idx (1+ idx))
                    )
                  )
                )

                ;; quebrar em várias polylines
                (setq runPts nil
                      runBulges nil
                      runType nil)

                (foreach s ord
                  (setq pStart (nth s pts))
                  (setq pEnd   (if (= s (1- n)) (car pts) (nth (1+ s) pts)))
                  (setq b      (nth s bulges))
                  (setq curType (nth s classes))

                  (if (null runPts)
                    (progn
                      (setq runPts (list pStart pEnd))
                      (setq runBulges (list b))
                      (setq runType curType)
                    )
                    (if (eq curType runType)
                      (progn
                        (setq runPts (append runPts (list pEnd)))
                        (setq runBulges (append runBulges (list b)))
                      )
                      (progn
                        (_flush-run
                          runPts
                          runBulges
                          styleLW
                          (if (eq runType 'CURVA_SEPARAR) 6 nil)
                        )
                        (setq runPts (list pStart pEnd))
                        (setq runBulges (list b))
                        (setq runType curType)
                      )
                    )
                  )
                )

                (_flush-run
                  runPts
                  runBulges
                  styleLW
                  (if (eq runType 'CURVA_SEPARAR) 6 nil)
                )

                (entdel ent)
              )
            )

            (setq i (1+ i))
          )

          (setq msg
            (strcat
              "\nProcessamento concluido."
              "\n----------------------------------"
              "\nTrechos retos: " (itoa lineCnt)
              "\nComprimento reto total: " (rtos lineLen 2 3)
              "\nCurvos mantidos (R > limite): " (itoa curveKeepCnt)
              "\nComprimento curvo mantido: " (rtos curveKeepLen 2 3)
              "\nCurvos separados (R <= limite): " (itoa curveSplitCnt)
              "\nComprimento curvo separado: " (rtos curveSplitLen 2 3)
              "\n----------------------------------"
            )
          )

          (prompt msg)
          (alert msg)
        )
      )
    )
  )
  (princ)
)