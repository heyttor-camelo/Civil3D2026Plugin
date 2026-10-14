;;@TITULO  Numeracao automatica de pranchas
;;@CATEGORIA Pranchas e layouts
;;@COMANDO NP
;;@DESC    Numera as pranchas pela ordem das abas de layout (01, 02... e o total /NN), usando textos nas
;;@DESC    layers PRANCHA_NUMERO e PRANCHA_TOTAL. O Model nao entra na contagem.
;;@USO     Digite NP
;;@USO     Configurar: selecione o texto do numero e o do total (primeira vez)
;;@USO     Atualizar: renumera depois de criar, apagar ou reordenar layouts
;; ----------------------------------------------------------------
(vl-load-com)

;; ============================================================
;; NUMERACAO AUTOMATICA DE PRANCHAS
;;
;; Layers:
;;   PRANCHA_NUMERO -> 01, 02, 03...
;;   PRANCHA_TOTAL  -> /01, /02, /03...
;;
;; Comando:
;;   NP
;;
;; Ao executar:
;;   [Configurar/Atualizar] <Atualizar>
;;
;; Configurar:
;;   Seleciona o texto do numero da prancha
;;   Seleciona o texto do total
;;   Associa automaticamente aos layers
;;   Atualiza todas as pranchas
;;
;; Atualizar:
;;   Apenas renumera os layouts
;;
;; Model nao entra na contagem.
;; A ordem e definida pela ordem visual das abas.
;; ============================================================


(setq *NP-LAYER-NUMERO* "PRANCHA_NUMERO")
(setq *NP-LAYER-TOTAL*  "PRANCHA_TOTAL")


;; ------------------------------------------------------------
;; Documento ativo
;; ------------------------------------------------------------

(defun NP:Documento ()
  (vla-get-ActiveDocument
    (vlax-get-acad-object)
  )
)


;; ------------------------------------------------------------
;; Criar layer
;; ------------------------------------------------------------

(defun NP:CriarLayer (nome / doc layers layer)

  (setq doc    (NP:Documento))
  (setq layers (vla-get-Layers doc))

  (if (tblsearch "LAYER" nome)

    (setq layer
      (vla-Item layers nome)
    )

    (setq layer
      (vla-Add layers nome)
    )
  )

  layer
)


(defun NP:CriarLayers ()

  (NP:CriarLayer *NP-LAYER-NUMERO*)
  (NP:CriarLayer *NP-LAYER-TOTAL*)

  (princ)
)


;; ------------------------------------------------------------
;; Formata numero
;;
;; 1  -> 01
;; 9  -> 09
;; 10 -> 10
;; ------------------------------------------------------------

(defun NP:FormatarNumero (n)

  (if (< n 10)
    (strcat "0" (itoa n))
    (itoa n)
  )
)


;; ------------------------------------------------------------
;; Verifica se entidade selecionada pode ser usada
;; ------------------------------------------------------------

(defun NP:EhTexto (obj / tipo)

  (setq tipo
    (vla-get-ObjectName obj)
  )

  (member
    tipo
    '(
      "AcDbText"
      "AcDbMText"
      "AcDbAttribute"
     )
  )
)


;; ------------------------------------------------------------
;; Seleciona texto e coloca no layer desejado
;; ------------------------------------------------------------

(defun NP:SelecionarTexto
  (mensagem layer / ent obj valido)

  (setq valido nil)

  (while (not valido)

    (setq ent
      (entsel mensagem)
    )

    (cond

      ;; Cancelou
      ((null ent)

       (setq valido 'cancelado)
      )


      ;; Selecionou algo
      (T

       (setq obj
         (vlax-ename->vla-object
           (car ent)
         )
       )

       (if (NP:EhTexto obj)

         (progn

           (vla-put-Layer obj layer)

           (setq valido T)
         )

         (princ
           "\nSelecione um TEXT, MTEXT ou atributo."
         )
       )
      )
    )
  )

  (if (= valido 'cancelado)
    nil
    obj
  )
)


;; ------------------------------------------------------------
;; Atualiza TEXT / MTEXT / ATTRIB
;; ------------------------------------------------------------

(defun NP:AtualizarObjeto
  (obj numero total / tipo layer attrs att qtd resultado)

  (setq qtd 0)

  (setq tipo
    (vla-get-ObjectName obj)
  )


  ;; ==========================================================
  ;; TEXT / MTEXT / ATTRIBUTE
  ;; ==========================================================

  (if
    (member
      tipo
      '(
        "AcDbText"
        "AcDbMText"
        "AcDbAttribute"
       )
    )

    (progn

      (setq layer
        (strcase
          (vla-get-Layer obj)
        )
      )


      ;; Numero da prancha
      (if
        (=
          layer
          (strcase *NP-LAYER-NUMERO*)
        )

        (progn

          (vla-put-TextString
            obj
            numero
          )

          (setq qtd
            (1+ qtd)
          )
        )
      )


      ;; Total de pranchas
      (if
        (=
          layer
          (strcase *NP-LAYER-TOTAL*)
        )

        (progn

          (vla-put-TextString
            obj
            total
          )

          (setq qtd
            (1+ qtd)
          )
        )
      )
    )
  )


  ;; ==========================================================
  ;; BLOCOS COM ATRIBUTOS
  ;; ==========================================================

  (if
    (and
      (= tipo "AcDbBlockReference")

      (vlax-property-available-p
        obj
        'HasAttributes
      )

      (=
        (vla-get-HasAttributes obj)
        :vlax-true
      )
    )

    (progn

      (setq resultado
        (vl-catch-all-apply
          'vla-GetAttributes
          (list obj)
        )
      )

      (if
        (not
          (vl-catch-all-error-p resultado)
        )

        (progn

          (setq attrs
            (vlax-safearray->list
              (vlax-variant-value resultado)
            )
          )

          (foreach att attrs

            (setq qtd
              (+
                qtd
                (NP:AtualizarObjeto
                  att
                  numero
                  total
                )
              )
            )
          )
        )
      )
    )
  )

  qtd
)


;; ------------------------------------------------------------
;; Atualiza um Layout
;; ------------------------------------------------------------

(defun NP:AtualizarLayout
  (layout numero total / bloco obj qtd)

  (setq qtd 0)

  (setq bloco
    (vla-get-Block layout)
  )

  (vlax-for obj bloco

    (setq qtd
      (+
        qtd
        (NP:AtualizarObjeto
          obj
          numero
          total
        )
      )
    )
  )

  qtd
)


;; ------------------------------------------------------------
;; Obtem layouts pela ordem das abas
;; ------------------------------------------------------------

(defun NP:ObterLayoutsOrdenados
  (/ doc layouts layout lista)

  (setq doc
    (NP:Documento)
  )

  (setq layouts
    (vla-get-Layouts doc)
  )

  (setq lista '())


  (vlax-for layout layouts

    (if
      (=
        (vla-get-ModelType layout)
        :vlax-false
      )

      (setq lista
        (cons layout lista)
      )
    )
  )


  (vl-sort
    lista
    '(lambda (a b)

       (<
         (vla-get-TabOrder a)
         (vla-get-TabOrder b)
       )
     )
  )
)


;; ------------------------------------------------------------
;; Renumera todas as pranchas
;; ------------------------------------------------------------

(defun NP:Atualizar
  (/ doc layouts total totalTxt indice layout numero qtd)

  (NP:CriarLayers)

  (setq doc
    (NP:Documento)
  )

  (setq layouts
    (NP:ObterLayoutsOrdenados)
  )

  (setq total
    (length layouts)
  )


  (if (> total 0)

    (progn

      (setq totalTxt
        (strcat
          "/"
          (NP:FormatarNumero total)
        )
      )

      (setq indice 1)
      (setq qtd 0)


      (vla-StartUndoMark doc)


      (foreach layout layouts

        (setq numero
          (NP:FormatarNumero indice)
        )

        (setq qtd
          (+
            qtd
            (NP:AtualizarLayout
              layout
              numero
              totalTxt
            )
          )
        )

        (setq indice
          (1+ indice)
        )
      )


      (vla-EndUndoMark doc)

      (vla-Regen doc 1)


      (princ
        (strcat
          "\n"
          (itoa total)
          " prancha(s) numerada(s). "
          (itoa qtd)
          " texto(s) atualizado(s)."
        )
      )
    )

    (princ
      "\nNenhum layout de Paper Space encontrado."
    )
  )

  (princ)
)


;; ------------------------------------------------------------
;; Configura os dois textos
;; ------------------------------------------------------------

(defun NP:Configurar
  (/ objNumero objTotal)

  (NP:CriarLayers)


  ;; Numero
  (setq objNumero
    (NP:SelecionarTexto
      "\nSelecione o texto do NUMERO DA PRANCHA: "
      *NP-LAYER-NUMERO*
    )
  )


  (if objNumero

    (progn

      (princ
        (strcat
          "\nAssociado ao layer: "
          *NP-LAYER-NUMERO*
        )
      )


      ;; Total
      (setq objTotal
        (NP:SelecionarTexto
          "\nSelecione o texto do TOTAL DE PRANCHAS: "
          *NP-LAYER-TOTAL*
        )
      )


      (if objTotal

        (progn

          (princ
            (strcat
              "\nAssociado ao layer: "
              *NP-LAYER-TOTAL*
            )
          )


          ;; Atualiza automaticamente
          (NP:Atualizar)
        )

        (princ
          "\nConfiguracao cancelada."
        )
      )
    )

    (princ
      "\nConfiguracao cancelada."
    )
  )

  (princ)
)


;; ============================================================
;; COMANDO NP
;; ============================================================

(defun c:NP
  (/ opcao)

  (vl-load-com)

  (NP:CriarLayers)


  (initget
    "Configurar Atualizar"
  )

  (setq opcao
    (getkword
      "\nO que deseja fazer? [Configurar/Atualizar] <Atualizar>: "
    )
  )


  ;; ENTER = Atualizar
  (if (null opcao)
    (setq opcao "Atualizar")
  )


  (cond

    ((= opcao "Configurar")
     (NP:Configurar)
    )

    ((= opcao "Atualizar")
     (NP:Atualizar)
    )
  )

  (princ)
)


;; ============================================================
;; AO CARREGAR
;; ============================================================

(NP:CriarLayers)

(princ
  "\nNumeracao de pranchas carregada. Comando: NP"
)

(princ)