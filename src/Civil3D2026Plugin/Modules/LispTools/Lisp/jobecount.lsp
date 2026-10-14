;;@TITULO  Numerar textos em sequencia
;;@CATEGORIA Blocos, textos e cores
;;@COMANDO JOBECOUNT
;;@DESC    Acrescenta um numero sequencial ao final de textos, MTEXT e atributos selecionados,
;;@DESC    na ordem de leitura: de cima para baixo e, na mesma linha, da esquerda para a direita.
;;@USO     Digite JOBECOUNT
;;@USO     Informe o numero inicial e o incremento
;;@USO     Selecione os textos
;; ----------------------------------------------------------------
(defun c:jobecount (/ ss entlst lst p1 p2 start_num increment i)

  ;; 1. Obtém o número inicial e o incremento
  (setq start_num (getint "\nDigite o numero inicial: "))
  (if (not start_num) (setq start_num 1))
  
  (setq increment (getint "\nDigite o incremento: "))
  (if (not increment) (setq increment 1))
  
  ;; 2. Seleciona os objetos a serem numerados
  (prompt "\nSelecione os textos, Mtexts e atributos de bloco para numerar: ")
  (setq ss (ssget '((0 . "TEXT,MTEXT,ATTDEF,ATTRIB"))))

  (if ss
    (progn
      ;; 3. Cria uma lista com a entidade e sua posição XY
      (setq i 0
            lst '())
      (repeat (sslength ss)
        (setq entlst (entget (ssname ss i)))
        (setq p1 (cdr (assoc 10 entlst))) ; Ponto de inserção do texto
        (setq lst (cons (list (cadr p1) (car p1) (ssname ss i)) lst))
        (setq i (1+ i))
      )
      
      ;; 4. Ordena a lista: maior Y primeiro, depois menor X
      (setq lst (vl-sort lst '(lambda (a b)
                               (if (equal (car a) (car b) 1e-4)
                                 (< (cadr a) (cadr b))
                                 (> (car a) (car b))
                               )
                             )
                  )
      )

      ;; 5. Numera os objetos
      (setq i 0)
      (foreach obj lst
        (setq obj_name (nth 2 obj)
              entlst (entget obj_name)
              obj_type (cdr (assoc 0 entlst))
              current_value (cdr (assoc 1 entlst))
              new_value (strcat current_value " " (itoa (+ start_num (* i increment))))
        )

        ;; Atualiza o objeto
        (cond
          ((equal "ATTRIB" obj_type)
            (entupd (subst (cons 1 new_value) (assoc 1 entlst) entlst))
          )
          ((equal "ATTDEF" obj_type)
            (entupd (subst (cons 1 new_value) (assoc 1 entlst) entlst))
          )
          ((or (equal "TEXT" obj_type) (equal "MTEXT" obj_type))
            (entmod (subst (cons 1 new_value) (assoc 1 entlst) entlst))
          )
        )
        (setq i (1+ i))
      )
      (princ (strcat "\n" (itoa i) " objetos numerados."))
    )
    (prompt "\nNenhum objeto selecionado.")
  )
  (princ)
)