;;@TITULO  Varios offsets em polilinhas fechadas
;;@CATEGORIA Polilinhas e geometria
;;@COMANDO MOFF
;;@VERSAO  1.0
;;@DESC    Faz varios offsets de uma vez em polilinhas fechadas, para dentro ou para fora, descobrindo o lado
;;@DESC    certo pela area (nao depende do sentido da polilinha).
;;@USO     Digite MOFF e selecione as polilinhas fechadas
;;@USO     Informe a distancia e a quantidade
;;@USO     Escolha Dentro ou Fora
;; ----------------------------------------------------------------
;;; ============================================================
;;; MOFF_v1.0.lsp
;;; Multiplos offsets em lote para polilinhas fechadas
;;;
;;; Comando: MOFF
;;;
;;; - Seleciona varias LWPOLYLINE / POLYLINE 2D fechadas
;;; - Informa distancia entre offsets
;;; - Informa quantidade de offsets
;;; - Escolhe Dentro ou Fora
;;; - Detecta automaticamente o lado correto pela area
;;; - Nao depende do sentido horario/anti-horario da polilinha
;;; ============================================================

(vl-load-com)

(defun moff:_delete-list (lst / obj)
  (foreach obj lst
    (if obj
      (vl-catch-all-apply 'vla-Delete (list obj))
    )
  )
)

(defun moff:_offset (obj dist / ret)
  (setq ret
    (vl-catch-all-apply
      'vlax-invoke
      (list obj 'Offset dist)
    )
  )
  (cond
    ((vl-catch-all-error-p ret) nil)
    ((= (type ret) 'LIST) ret)
    ((= (type ret) 'VLA-OBJECT) (list ret))
    (t nil)
  )
)

(defun moff:_area-list (lst / total obj a)
  (setq total 0.0)
  (foreach obj lst
    (if (and obj (vlax-property-available-p obj 'Area))
      (progn
        (setq a (vl-catch-all-apply 'vla-get-Area (list obj)))
        (if (not (vl-catch-all-error-p a))
          (setq total (+ total (abs a)))
        )
      )
    )
  )
  total
)

(defun moff:_valid-poly-p (obj / name closed)
  (setq name (vla-get-ObjectName obj))
  (if
    (member name '("AcDbPolyline" "AcDb2dPolyline"))
    (progn
      (setq closed
        (vl-catch-all-apply 'vla-get-Closed (list obj))
      )
      (and
        (not (vl-catch-all-error-p closed))
        (= closed :vlax-true)
      )
    )
    nil
  )
)

(defun moff:_source-area (obj / a)
  (setq a (vl-catch-all-apply 'vla-get-Area (list obj)))
  (if (vl-catch-all-error-p a)
    nil
    (abs a)
  )
)

;;; Retorna: (sinal lista-de-objetos)
;;; sinal = 1.0 ou -1.0
(defun moff:_first-offset (obj dist dir / a0 plus minus ap am tol choose sign)
  (setq a0 (moff:_source-area obj))

  (if (or (null a0) (<= a0 1e-12))
    nil
    (progn
      (setq plus  (moff:_offset obj dist))
      (setq minus (moff:_offset obj (- dist)))

      (setq ap (if plus  (moff:_area-list plus)  nil))
      (setq am (if minus (moff:_area-list minus) nil))
      (setq tol (max 1e-10 (* a0 1e-10)))

      (cond
        ;; Os dois lados foram gerados: menor area = dentro; maior area = fora.
        ((and plus minus)
          (if (= dir "Dentro")
            (if (<= ap am)
              (progn
                (setq choose plus sign 1.0)
                (moff:_delete-list minus)
              )
              (progn
                (setq choose minus sign -1.0)
                (moff:_delete-list plus)
              )
            )
            (if (>= ap am)
              (progn
                (setq choose plus sign 1.0)
                (moff:_delete-list minus)
              )
              (progn
                (setq choose minus sign -1.0)
                (moff:_delete-list plus)
              )
            )
          )
        )

        ;; Somente +DIST funcionou: valida pelo aumento/reducao de area.
        (plus
          (if
            (or
              (and (= dir "Dentro") (< ap (- a0 tol)))
              (and (= dir "Fora")   (> ap (+ a0 tol)))
            )
            (setq choose plus sign 1.0)
            (moff:_delete-list plus)
          )
        )

        ;; Somente -DIST funcionou: valida pelo aumento/reducao de area.
        (minus
          (if
            (or
              (and (= dir "Dentro") (< am (- a0 tol)))
              (and (= dir "Fora")   (> am (+ a0 tol)))
            )
            (setq choose minus sign -1.0)
            (moff:_delete-list minus)
          )
        )
      )

      (if choose
        (list sign choose)
        nil
      )
    )
  )
)

(defun moff:_get-distance (/ val)
  (if *moff-dist*
    (progn
      (initget 6)
      (setq val
        (getdist
          (strcat
            "\nDistancia entre offsets <"
            (rtos *moff-dist* 2 4)
            ">: "
          )
        )
      )
      (if val
        (setq *moff-dist* val)
      )
    )
    (progn
      (initget 7)
      (setq *moff-dist* (getdist "\nDistancia entre offsets: "))
    )
  )
  *moff-dist*
)

(defun moff:_get-quantity (/ val)
  (if (null *moff-qtd*)
    (setq *moff-qtd* 1)
  )
  (initget 6)
  (setq val
    (getint
      (strcat
        "\nQuantidade de offsets <"
        (itoa *moff-qtd*)
        ">: "
      )
    )
  )
  (if val
    (setq *moff-qtd* val)
  )
  *moff-qtd*
)

(defun moff:_get-direction (/ val)
  (if (null *moff-dir*)
    (setq *moff-dir* "Fora")
  )
  (initget "Dentro Fora")
  (setq val
    (getkword
      (strcat
        "\nDirecao [Dentro/Fora] <"
        *moff-dir*
        ">: "
      )
    )
  )
  (if val
    (setq *moff-dir* val)
  )
  *moff-dir*
)

(defun c:MOFF (/ *error* acad doc ss dist qtd dir i ent obj first sign created failed skipped n more a0 anew tol)
  (vl-load-com)

  (setq acad (vlax-get-acad-object))
  (setq doc  (vla-get-ActiveDocument acad))

  (defun *error* (msg)
    (if doc
      (vl-catch-all-apply 'vla-EndUndoMark (list doc))
    )
    (if
      (and msg
           (/= msg "Function cancelled")
           (/= msg "quit / exit abort")
      )
      (princ (strcat "\nErro: " msg))
    )
    (princ)
  )

  (princ "\nMOFF - Multiplos offsets em polilinhas fechadas.")

  (setq dist (moff:_get-distance))
  (setq qtd  (moff:_get-quantity))
  (setq dir  (moff:_get-direction))

  (prompt "\nSelecione as polilinhas fechadas: ")
  (setq ss (ssget '((0 . "LWPOLYLINE,POLYLINE"))))

  (if (null ss)
    (princ "\nNenhuma polilinha selecionada.")
    (progn
      (vla-StartUndoMark doc)

      (setq created 0
            failed  0
            skipped 0
            i       0)

      (repeat (sslength ss)
        (setq ent (ssname ss i))
        (setq obj (vlax-ename->vla-object ent))

        (if (moff:_valid-poly-p obj)
          (progn
            ;; Primeiro offset: testa os dois sinais para descobrir o lado.
            (setq first (moff:_first-offset obj dist dir))

            (if first
              (progn
                (setq sign (car first))
                (setq created (+ created (length (cadr first))))

                ;; Demais offsets: usa diretamente o sinal ja descoberto.
                (setq n 2)
                (while (<= n qtd)
                  (setq more
                    (moff:_offset obj (* sign dist n))
                  )

                  (if more
                    (progn
                      ;; Checagem simples de seguranca pela area.
                      (setq a0   (moff:_source-area obj))
                      (setq anew (moff:_area-list more))
                      (setq tol  (max 1e-10 (* a0 1e-10)))

                      (if
                        (or
                          (and (= dir "Dentro") (< anew (- a0 tol)))
                          (and (= dir "Fora")   (> anew (+ a0 tol)))
                        )
                        (setq created (+ created (length more)))
                        (progn
                          (moff:_delete-list more)
                          (setq failed (1+ failed))
                        )
                      )
                    )
                    (setq failed (1+ failed))
                  )

                  (setq n (1+ n))
                )
              )
              (setq failed (+ failed qtd))
            )
          )
          (setq skipped (1+ skipped))
        )

        (setq i (1+ i))
      )

      (vla-EndUndoMark doc)

      (princ
        (strcat
          "\nMOFF concluido. "
          (itoa created) " offset(s) criado(s)"
          (if (> failed 0)
            (strcat "; " (itoa failed) " tentativa(s) sem geometria valida")
            ""
          )
          (if (> skipped 0)
            (strcat "; " (itoa skipped) " objeto(s) ignorado(s) por nao serem polilinhas 2D fechadas")
            ""
          )
          "."
        )
      )
    )
  )

  (princ)
)

(defun c:MOFFSET () (c:MOFF))

(princ "\nMOFF v1.0 carregado. Comandos: MOFF / MOFFSET")
(princ)
