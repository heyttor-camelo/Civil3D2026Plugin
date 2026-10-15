;;@TITULO  Carimbo de as built (JOBE)
;;@CATEGORIA Pranchas e layouts
;;@ICONE prancha
;;@COMANDO JOBEPLACE
;;@DESC    Atualiza carimbos para as built: preenche atributos fixos (REV, DATA, COMENTARIO, DISCIPLINA)
;;@DESC    e troca termos nos textos (PROJETADO -> EXECUTADO, A SER IMPLANTADO -> IMPLANTADO etc.).
;;@DESC    ATENCAO: os valores estao fixos no codigo (datas de 24/11/2025, 'AS BUILT DE GEOMETRIA') e a
;;@DESC    rotina APAGA todas as referencias do bloco XREF_PE_T_BRT-T1_GEO_b. Revise antes de usar em outro projeto.
;;@USO     Digite JOBEPLACE
;;@USO     Selecione os blocos de carimbo e os textos a atualizar
;; ----------------------------------------------------------------
;; ========== FUNÇÕES AUXILIARES ==========

(defun str-replace (old new str / pos lenOld)
  ;; Troca TODAS as ocorrências de OLD por NEW em STR
  (if (and str old new)
    (progn
      (setq lenOld (strlen old))
      (while (setq pos (vl-string-search old str)) ; pos = 0-based
        (setq str
               (strcat
                 (substr str 1 pos)             ; antes
                 new                            ; novo
                 (substr str (+ pos lenOld 1))  ; resto
               )
        )
      )
    )
  )
  str
)

(defun apply-replaces (txt)
  (if txt
    (progn
      ;; mais específicos primeiro
      (setq txt (str-replace "A SER IMPLANTADO" "IMPLANTADO" txt))
      (setq txt (str-replace "EXISTENTE P/ REMOÇÃO" "REMOVIDA" txt))

      ;; removendo "A SER"
      (setq txt (str-replace "A SER " "" txt))
      (setq txt (str-replace "A SER" "" txt))

      ;; projetado / projetada
      (setq txt (str-replace "PROJETADA" "EXECUTADA" txt))
      (setq txt (str-replace "PROJETADO" "EXECUTADO" txt))

      ;; proposto / proposta some
      (setq txt (str-replace "PROPOSTO" "" txt))
      (setq txt (str-replace "PROPOSTA" "" txt))
    )
  )
  txt
)

(defun ReplaceTextEnt (ename / data tipo txt)
  (if ename
    (progn
      (setq data (entget ename)
            tipo (cdr (assoc 0 data)))
      (cond
        ((= tipo "TEXT")
         (setq txt (cdr (assoc 1 data)))
         (setq txt (apply-replaces txt))
         (if txt
           (progn
             (setq data (subst (cons 1 txt) (assoc 1 data) data))
             (entmod data)
           )
         )
        )
        ((= tipo "MTEXT")
         (setq txt (cdr (assoc 1 data)))
         (setq txt (apply-replaces txt))
         (if txt
           (progn
             (setq data (subst (cons 1 txt) (assoc 1 data) data))
             (entmod data)
           )
         )
        )
      )
    )
  )
)

(defun ReplaceTextInBlockDef (blkName / btr e)
  (if (and blkName
           (setq btr (tblobjname "BLOCK" blkName)))
    (progn
      (setq e (entnext btr))
      (while (and e
                  (/= "ENDBLK" (cdr (assoc 0 (entget e)))))
        (ReplaceTextEnt e)
        (setq e (entnext e))
      )
    )
  )
)

(defun DelInSpace (space blkName / obj)
  ;; Apaga referências de um bloco num "space" (Model ou Layout.Block)
  (vlax-for obj space
    (if (and (= (vla-get-ObjectName obj) "AcDbBlockReference")
             (= (strcase (vla-get-EffectiveName obj))
                (strcase blkName)))
      (vla-Delete obj)
    )
  )
)

(defun DeleteBlockAllSpaces (blkName / acad doc)
  ;; Apaga referências em Model + todos os Layouts
  (setq acad (vlax-get-acad-object)
        doc  (vla-get-ActiveDocument acad)
  )
  (DelInSpace (vla-get-ModelSpace doc) blkName)
  (vlax-for lay (vla-get-Layouts doc)
    (if (/= (strcase (vla-get-Name lay)) "MODEL")
      (DelInSpace (vla-get-Block lay) blkName)
    )
  )
)

;; ========== COMANDO PRINCIPAL ==========

(defun c:JOBEPLACE ( / ss i n ent type
                      eBloco e att tag par
                      oldVal baseVal newVal
                      tagsValores doneBlocks blkName
                      blkToErase)

  (vl-load-com)

  ;; Tabela de TAGs fixas
  (setq tagsValores
    '(
      ("REV_1" . "0")
      ("REV_2" . "")
      ("REV_3" . "")
      ("DATA_1" . "24/11/25")  ; << atualizado
      ("DATA_2" . "")
      ("DATA_3" . "")
      ("COMENTARIO_1" . "EMISSÃO INICIAL")
      ("COMENTARIO_2" . "")
      ("COMENTARIO_3" . "")
      ("ELAB_2" . "")
      ("ELAB_3" . "")
      ("VERIF_2" . "")
      ("APROV_2" . "")
      ("VERIF_3" . "")
      ("APROV_3" . "")
      ("DISCIPLINA" . "AS BUILT DE GEOMETRIA")
      ("DATA" . "24/11/2025")
      ("REV" . "0")
    )
  )

  ;; 1) Apagar todas as referências do bloco em qualquer espaço
  (setq blkToErase "XREF_PE_T_BRT-T1_GEO_b")
  (princ (strcat "\nApagando todas as referências do bloco: " blkToErase))
  (DeleteBlockAllSpaces blkToErase)

  ;; 2) Processar seleção (atributos + textos)
  (princ "\nSelecione blocos e/ou textos a serem atualizados: ")
  (if (setq ss (ssget))
    (progn
      (setq n          (sslength ss)
            i          0
            doneBlocks '()
      )

      (while (< i n)
        (setq ent  (ssname ss i)
              type (cdr (assoc 0 (entget ent)))
        )

        (cond
          ;; ======== se for bloco (INSERT) ========
          ((= type "INSERT")
           (setq eBloco ent)

           ;; 1) atributos do bloco
           (setq e (entnext eBloco))
           (while (and e
                       (/= "SEQEND" (cdr (assoc 0 (entget e)))))
             (setq att (entget e))
             (if (= "ATTRIB" (cdr (assoc 0 att)))
               (progn
                 (setq tag    (cdr (assoc 2 att)))
                 (setq oldVal (cdr (assoc 1 att)))
                 (setq par    (assoc tag tagsValores))

                 ;; valor base:
                 ;;   se TAG está na lista -> valor fixo
                 ;;   senão                 -> valor atual
                 (setq baseVal (if par (cdr par) oldVal))

                 ;; aplica os replaces em cima do baseVal
                 (setq newVal (apply-replaces baseVal))

                 ;; grava somente se mudou
                 (if (/= newVal oldVal)
                   (progn
                     (setq att (subst (cons 1 newVal)
                                      (assoc 1 att)
                                      att))
                     (entmod att)
                   )
                 )
               )
             )
             (setq e (entnext e))
           )
           (entupd eBloco)

           ;; 2) textos normais dentro da definição do bloco
           (setq blkName (cdr (assoc 2 (entget eBloco))))
           (if (and blkName (not (member blkName doneBlocks)))
             (progn
               (ReplaceTextInBlockDef blkName)
               (setq doneBlocks (cons blkName doneBlocks))
             )
           )
          )

          ;; ======== TEXT / MTEXT soltos ========
          ((or (= type "TEXT") (= type "MTEXT"))
           (ReplaceTextEnt ent)
          )
        )

        (setq i (1+ i))
      )

      (princ
        (strcat
          "\nProcessados "
          (itoa n)
          " objetos da seleção."
        )
      )
    )
    (princ "\nNada selecionado.")
  )

  (princ)
)
