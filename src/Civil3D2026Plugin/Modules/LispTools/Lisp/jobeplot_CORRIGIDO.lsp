;;@TITULO  Plotagem em lote por bloco de folha
;;@CATEGORIA Pranchas e layouts
;;@COMANDO JOBEPLOT
;;@DESC    Plota em PDF cada bloco de folha (A4 a A0) do Model, usando o contorno do bloco como janela.
;;@DESC    As folhas sao ordenadas de cima para baixo e da esquerda para a direita, e os PDFs saem numerados
;;@DESC    na pasta do DWG, com o CTB do layout atual.
;;@USO     Digite JOBEPLOT
;;@USO     Informe o formato (A3, A2, A1...)
;;@USO     Selecione os blocos de folha
;; ----------------------------------------------------------------
;;; ================================================================
;;; JOBEPLOT - Plotagem em lote por blocos de formato
;;;
;;; Objetivo:
;;;   Selecionar blocos de folhas (A4/A3/A2/A1/A0), ordenar as folhas
;;;   pela posicao no desenho e exportar cada uma para um PDF separado.
;;;
;;; Processo:
;;;   1. Pergunta o formato da folha.
;;;   2. Seleciona INSERTs com o nome desse formato.
;;;   3. Ordena de cima para baixo e da esquerda para a direita.
;;;   4. Usa o bounding box de cada bloco como janela de plotagem.
;;;   5. Gera PDFs numerados na mesma pasta do DWG.
;;;
;;; Correcao desta versao:
;;;   O arquivo original continha centenas de espacos U+00A0
;;;   (non-breaking spaces), que provocavam syntax error no AutoLISP.
;;; ================================================================

(vl-load-com)

(defun c:jobeplot (/ dwg file hnd i len llpt mn mx ss tab urpt inspt insx insy lst formato folha-nome ctb-nome x)

  ;; ---- 1. Pergunta o formato da folha ----
  (setq formato (getstring T "\nInforme o formato da folha (ex: A3, A2, A1): "))
  (if (or (null formato) (= formato ""))
    (setq formato "A3")
  )
  (setq formato (strcase formato))

  ;; ---- 2. Define o nome completo do formato de impressao ----
  (cond
    ((equal (strcase formato) "A4") (setq folha-nome "ISO full bleed A4 (297.00 x 210.00 MM)"))
    ((equal (strcase formato) "A3") (setq folha-nome "ISO full bleed A3 (420.00 x 297.00 MM)"))
    ((equal (strcase formato) "A2") (setq folha-nome "ISO full bleed A2 (594.00 x 420.00 MM)"))
    ((equal (strcase formato) "A1") (setq folha-nome "ISO full bleed A1 (841.00 x 594.00 MM)"))
    ((equal (strcase formato) "A0") (setq folha-nome "ISO full bleed A0 (1189.00 x 841.00 MM)"))
    (t (setq folha-nome "ISO full bleed A3 (420.00 x 297.00 MM)"))
  )

  ;; ---- 3. Obtem o nome do CTB do layout atual ----
  (setq ctb-nome (vla-get-stylesheet (vla-get-activelayout (vla-get-activedocument (vlax-get-acad-object)))))
  
  ;; ---- 4. Garante UCS Mundial ----
  (command "_.UCS" "_W")

  ;; ---- 5. Selecao manual das folhas ----
  (prompt (strcat "\nSelecione as folhas do formato " formato " que deseja plotar: "))
  (setq ss (ssget "_:L" (list '(0 . "INSERT") (cons 2 formato))))

  (if ss
    (progn
      (setq lst '())
      ;; ---- 6. Cria lista com Y e X de insercao, tab e handle ----
      (repeat (setq i (sslength ss))
        (setq hnd (ssname ss (setq i (1- i))))
        (setq tab (cdr (assoc 410 (entget hnd))))
        (setq inspt (vlax-get (vlax-ename->vla-object hnd) 'InsertionPoint))
        (setq insx (car inspt))
        (setq insy (cadr inspt))
        (setq lst (cons (list insy insx tab hnd) lst))
      )

      ;; ---- 7. Ordena por Y (maior -> menor) e X (menor -> maior) ----
      (setq lst
        (vl-sort lst
          '(lambda (a b)
             (if (/= (car a) (car b))
               (> (car a) (car b))   ; prioridade eixo Y (mais alto primeiro)
               (< (cadr a) (cadr b)) ; esquerda -> direita
             )
           )
        )
      )

      ;; ---- 8. Plotagem ----
      (setq i 0)
      (foreach x lst
        (setq file (strcat
                     (getvar 'DWGPREFIX)
                     (vl-filename-base (getvar 'DWGNAME))
                     "-"
                     (itoa (setq i (1+ i)))
                     ".pdf"
                   )
        )

        (if (findfile file) (vl-file-delete file))

        ;; ---- Bounding box ----
        (vla-getboundingbox (vlax-ename->vla-object (nth 3 x)) 'mn 'mx)
        (setq llpt (vlax-safearray->list mn)
              urpt (vlax-safearray->list mx))

        ;; ---- Comando de plot ----
        (command "-plot"
                 "yes"
                 (nth 2 x)
                 "DWG TO PDF.PC3"
                 folha-nome
                 "Millimeters"
                 "Landscape"
                 "No"
                 "Window"
                 llpt
                 urpt
                 "Fit"
                 "Center"
                 "yes"
                 ctb-nome ; Usa o nome do CTB do desenho atual
                 "yes"
                 ""
        )

        (command file "no" "Yes")
      )

      (prompt (strcat "\nPlotagem concluida: " (itoa i) " folhas exportadas."))
    )
    (prompt "\nNenhuma folha selecionada.")
  )

  (princ)
)

(princ "\nJOBEPLOT carregado. Digite JOBEPLOT para executar.")
(princ)
