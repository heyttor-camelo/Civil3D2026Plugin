;;@TITULO  Zebrado em area fechada
;;@CATEGORIA Vias e meio-fio
;;@ICONE zebra
;;@COMANDO ZEBRA
;;@VERSAO  1.5
;;@DESC    Preenche uma polilinha fechada com zebrado (faixas inclinadas e contorno) como UMA unica hachura
;;@DESC    SOLID, na layer ZEBRADO, sem alterar o contorno original.
;;@USO     Digite ZEBRA e selecione a polilinha fechada
;;@USO     Informe espacamento, espessura das faixas e do contorno
;;@USO     Informe o angulo (45 graus por padrao)
;; ----------------------------------------------------------------
;;; ============================================================
;;; ZEBRA v1.5
;;; Zebrado SOLID dentro de uma LWPOLYLINE fechada.
;;;
;;; - Pede espacamento LIVRE entre faixas
;;; - Pede espessura das faixas
;;; - Pede espessura do contorno <igual a espessura da faixa>
;;; - Pede angulo <45 graus>
;;; - Cria/usa o layer ZEBRADO
;;; - Resultado final = UMA UNICA HATCH SOLID
;;; - Contorno original nao e alterado
;;;
;;; ESTRATEGIA v1.5:
;;;   A HATCH nasce preenchendo TODO o contorno externo. Os VAOS entre
;;;   as faixas sao recortados pela regiao interna (offset do contorno)
;;;   e adicionados como INNER LOOPS.
;;;
;;;   CORRECAO PRINCIPAL DA v1.5:
;;;   A falha do vao #44 mostrou que o problema nao era simplesmente
;;;   "duas ilhas desconectadas". O recorte pode gerar loops que se tocam
;;;   apenas em um vertice. Nesse caso, agrupar tudo por CONECTIVIDADE
;;;   produz um unico grupo tipo "8", que AddRegion rejeita.
;;;
;;;   Agora, quando um InnerLoop direto falha, a REGION e explodida em
;;;   curvas e a rotina monta um GRAFO de arestas. Em vez de componentes
;;;   conexos, ela extrai CICLOS FECHADOS independentes pelo menor caminho
;;;   entre as extremidades de cada aresta. Assim, dois loops que encostam
;;;   apenas em um ponto continuam sendo dois loops separados.
;;;
;;;   Os ciclos sao enviados DIRETAMENTE ao AppendInnerLoop como arrays
;;;   de Line/Arc/Polyline/etc.; nao dependemos mais de AddRegion para
;;;   reconstruir os pedacos do vao.
;;;
;;; Comando: ZEBRA
;;; ============================================================

(vl-load-com)

(setq *zb-layer* "ZEBRADO")

(defun zb:safe-delete (obj)
  (if (and obj (= (type obj) 'VLA-OBJECT))
    (vl-catch-all-apply 'vla-Delete (list obj))
  )
)

(defun zb:delete-list (lst)
  (foreach o lst (zb:safe-delete o))
)

(defun zb:obj-array1 (obj / a)
  (setq a (vlax-make-safearray vlax-vbObject '(0 . 0)))
  (vlax-safearray-put-element a 0 obj)
  a
)

(defun zb:region-from-object (space obj / ret sa)
  (setq ret (vl-catch-all-apply 'vla-AddRegion (list space (zb:obj-array1 obj))))
  (if (vl-catch-all-error-p ret)
    nil
    (progn
      (setq sa (vlax-variant-value ret))
      (vlax-safearray-get-element sa 0)
    )
  )
)

(defun zb:flat-xy (pts / out)
  (setq out '())
  (foreach p pts
    (setq out (cons (cadr p) (cons (car p) out)))
  )
  (reverse out)
)

(defun zb:add-closed-lwpoly (space pts elev / a pl vals)
  (setq vals (zb:flat-xy pts))
  (setq a (vlax-make-safearray vlax-vbDouble (cons 0 (1- (length vals)))))
  (vlax-safearray-fill a vals)
  (setq pl (vla-AddLightWeightPolyline space a))
  (vla-put-Closed pl :vlax-true)
  (if (vlax-property-available-p pl 'Elevation T)
    (vla-put-Elevation pl elev)
  )
  pl
)

(defun zb:dot2 (p v)
  (+ (* (car p) (car v)) (* (cadr p) (cadr v)))
)

(defun zb:pt-from-tu (tval nval u n)
  (list (+ (* tval (car u)) (* nval (car n)))
        (+ (* tval (cadr u)) (* nval (cadr n))))
)

(defun zb:minmax-proj-bbox (obj u / pmin pmax mn mx corners q d)
  (vla-GetBoundingBox obj 'pmin 'pmax)
  (setq pmin (vlax-safearray->list pmin)
        pmax (vlax-safearray->list pmax))
  (setq corners
    (list
      (list (car pmin) (cadr pmin))
      (list (car pmin) (cadr pmax))
      (list (car pmax) (cadr pmin))
      (list (car pmax) (cadr pmax))))
  (setq d (zb:dot2 (car corners) u)
        mn d
        mx d)
  (foreach q (cdr corners)
    (setq d (zb:dot2 q u))
    (if (< d mn) (setq mn d))
    (if (> d mx) (setq mx d))
  )
  (list mn mx)
)

(defun zb:minmax-proj-curve (ename n / ep segs samples i prm p d mn mx)
  ;; Amostragem apenas para descobrir o range do padrao.
  ;; O recorte real e feito por REGION, preservando arcos/bulges.
  (setq ep (vlax-curve-getEndParam ename))
  (setq segs (max 1 (fix (+ ep 0.5))))
  (setq samples (max 200 (min 4000 (* 8 segs))))
  (setq i 0 mn nil mx nil)
  (repeat (1+ samples)
    (setq prm (* ep (/ (float i) samples)))
    (setq p (vlax-curve-getPointAtParam ename prm))
    (if p
      (progn
        (setq d (+ (* (car p) (car n)) (* (cadr p) (cadr n))))
        (if (or (null mn) (< d mn)) (setq mn d))
        (if (or (null mx) (> d mx)) (setq mx d))
      )
    )
    (setq i (1+ i))
  )
  (list mn mx)
)

(defun zb:ensure-layer (doc name / lays lay)
  (setq lays (vla-get-Layers doc))
  (setq lay (vl-catch-all-apply 'vla-Item (list lays name)))
  (if (vl-catch-all-error-p lay)
    (setq lay (vla-Add lays name))
  )
  lay
)

(defun zb:variant-objects (v / x)
  ;; Converte o retorno de Offset para lista de VLA-OBJECTS.
  (cond
    ((vl-catch-all-error-p v) nil)
    ((= (type v) 'VARIANT)
      (setq x (vlax-variant-value v))
      (cond
        ((= (type x) 'SAFEARRAY) (vlax-safearray->list x))
        ((= (type x) 'VLA-OBJECT) (list x))
        (T nil)))
    ((= (type v) 'SAFEARRAY) (vlax-safearray->list v))
    ((= (type v) 'VLA-OBJECT) (list v))
    (T nil))
)

(defun zb:closed-area (obj / a)
  (if (and obj
           (= (type obj) 'VLA-OBJECT)
           (vlax-property-available-p obj 'Area))
    (progn
      (setq a (vl-catch-all-apply 'vla-get-Area (list obj)))
      (if (vl-catch-all-error-p a) nil a)
    )
    nil
  )
)

(defun zb:inward-offset (obj dist / area0 pos neg all o a best bestA)
  ;; Tenta OFFSET nos dois sentidos e escolhe a curva fechada de maior
  ;; area que ainda seja menor que a original.
  (setq area0 (zb:closed-area obj))
  (setq pos (zb:variant-objects
              (vl-catch-all-apply 'vla-Offset (list obj dist))))
  (setq neg (zb:variant-objects
              (vl-catch-all-apply 'vla-Offset (list obj (- dist)))))
  (setq all (append pos neg)
        best nil
        bestA -1.0)

  (foreach o all
    (setq a (zb:closed-area o))
    (if (and a area0 (> a 1e-12) (< a (- area0 1e-10)))
      (if (> a bestA)
        (setq best o bestA a)
      )
    )
  )

  (foreach o all
    (if (not (eq o best)) (zb:safe-delete o))
  )
  best
)

(defun zb:new-solid-hatch (space layer / h)
  ;; 0 = acHatchPatternTypePreDefined
  ;; :vlax-false = nao associativa; permite apagar auxiliares depois.
  (setq h (vla-AddHatch space 0 "SOLID" :vlax-false))
  (if layer (vla-put-Layer h layer))
  h
)

(defun zb:append-outer (h obj / err)
  (setq err
    (vl-catch-all-apply
      'vla-AppendOuterLoop
      (list h (zb:obj-array1 obj))))
  (not (vl-catch-all-error-p err))
)

(defun zb:append-inner (h obj / err)
  (setq err
    (vl-catch-all-apply
      'vla-AppendInnerLoop
      (list h (zb:obj-array1 obj))))
  (not (vl-catch-all-error-p err))
)

(defun zb:append-inner-objs (h objs / err)
  ;; Um InnerLoop pode ser formado por varias curvas, desde que suas
  ;; extremidades componham um ciclo fechado.
  (setq err
    (vl-catch-all-apply
      'vla-AppendInnerLoop
      (list h (zb:obj-array objs))))
  (not (vl-catch-all-error-p err))
)


(defun zb:obj-array (objs / a i)
  ;; SAFEARRAY de VLA-OBJECTS para chamadas ActiveX que aceitam varios objetos.
  (if objs
    (progn
      (setq a (vlax-make-safearray vlax-vbObject (cons 0 (1- (length objs))))
            i 0)
      (foreach o objs
        (vlax-safearray-put-element a i o)
        (setq i (1+ i))
      )
      a
    )
    nil
  )
)

(defun zb:regions-from-objects (space objs / ret)
  ;; AddRegion cria UMA REGION PARA CADA LOOP FECHADO encontrado.
  ;; Isto e justamente o que precisamos quando um recorte concavo gera
  ;; varias ilhas desconectadas dentro do mesmo vao.
  (if objs
    (progn
      (setq ret
        (vl-catch-all-apply
          'vla-AddRegion
          (list space (zb:obj-array objs))))
      (if (vl-catch-all-error-p ret)
        nil
        (zb:variant-objects ret)
      )
    )
    nil
  )
)

(defun zb:curve-ends (obj / en sp ep)
  ;; Retorna (start end) em WCS para qualquer objeto aceito por vlax-curve.
  (setq en (vlax-vla-object->ename obj))
  (setq sp (vl-catch-all-apply 'vlax-curve-getStartPoint (list en)))
  (setq ep (vl-catch-all-apply 'vlax-curve-getEndPoint   (list en)))
  (if (or (vl-catch-all-error-p sp)
          (vl-catch-all-error-p ep)
          (null sp)
          (null ep))
    nil
    (list sp ep)
  )
)

(defun zb:pts-close-p (a b tol)
  (and a b (<= (distance a b) tol))
)

(defun zb:remove-id (id items / out it)
  (setq out '())
  (foreach it items
    (if (/= id (car it))
      (setq out (cons it out))
    )
  )
  (reverse out)
)

(defun zb:remove-ids (ids items / out it)
  (setq out '())
  (foreach it items
    (if (not (member (car it) ids))
      (setq out (cons it out))
    )
  )
  (reverse out)
)

(defun zb:edge-other-point (edge pt tol / sp ep)
  ;; edge = (id obj start end)
  (setq sp (caddr edge)
        ep (cadddr edge))
  (cond
    ((zb:pts-close-p pt sp tol) ep)
    ((zb:pts-close-p pt ep tol) sp)
    (T nil)
  )
)

(defun zb:edge-touches-point-p (edge pt tol)
  (or
    (zb:pts-close-p pt (caddr edge) tol)
    (zb:pts-close-p pt (cadddr edge) tol))
)

(defun zb:queue-push (q x)
  ;; Para os poucos segmentos de um recorte, append e suficientemente leve
  ;; e deixa o BFS mais legivel.
  (append q (list x))
)

(defun zb:shortest-edge-path (startPt targetPt edges tol
                              / queue state cur path used edge eid nxt newpath newused found)
  ;; BFS em grafo de arestas NAO direcionadas.
  ;; Retorna a menor sequencia de edges que liga startPt a targetPt.
  ;; Isso e importante quando dois loops se tocam num vertice: o menor
  ;; ciclo fecha uma das ilhas, em vez de atravessar as duas como um "8".
  (setq queue (list (list startPt '() '()))
        found nil)

  (while (and queue (null found))
    (setq state (car queue)
          queue (cdr queue)
          cur   (car state)
          path  (cadr state)
          used  (caddr state))

    (foreach edge edges
      (setq eid (car edge))
      (if (and (null found)
               (not (member eid used))
               (zb:edge-touches-point-p edge cur tol))
        (progn
          (setq nxt (zb:edge-other-point edge cur tol))
          (if nxt
            (progn
              (setq newpath (append path (list edge))
                    newused (cons eid used))
              (if (zb:pts-close-p nxt targetPt tol)
                (setq found newpath)
                (setq queue
                  (zb:queue-push queue
                    (list nxt newpath newused)))
              )
            )
          )
        )
      )
    )
  )
  found
)

(defun zb:make-edge-items (curves / out id o ee)
  (setq out '()
        id 0)
  (foreach o curves
    (setq ee (zb:curve-ends o))
    (if ee
      (setq out
        (cons
          (list id o (car ee) (cadr ee))
          out))
    )
    (setq id (1+ id))
  )
  (reverse out)
)

(defun zb:extract-cycles (curves tol / edges remaining seed rest path cycle
                                      ids cycles failed e)
  ;; Decompoe o conjunto de curvas em CICLOS edge-disjoint.
  ;; Diferente da v1.4, um vertice compartilhado por dois loops nao faz
  ;; com que os dois sejam fundidos num mesmo componente.
  (setq edges (zb:make-edge-items curves)
        remaining edges
        cycles '()
        failed nil)

  ;; Se alguma curva explodida nao forneceu endpoints, nao arriscamos.
  (if (/= (length edges) (length curves))
    nil
    (progn
      (while (and remaining (not failed))
        (setq seed (car remaining)
              rest (zb:remove-id (car seed) remaining))

        ;; Procura o menor caminho que fecha o seed.
        (setq path
          (zb:shortest-edge-path
            (cadddr seed)   ; end do seed
            (caddr seed)    ; start do seed
            rest
            tol))

        (if (null path)
          (setq failed T)
          (progn
            (setq cycle (cons seed path)
                  ids (mapcar 'car cycle))

            ;; Guarda apenas os objetos VLA, na ordem encontrada.
            (setq cycles
              (cons
                (mapcar '(lambda (x) (cadr x)) cycle)
                cycles))

            (setq remaining (zb:remove-ids ids remaining))
          )
        )
      )

      (if failed nil (reverse cycles))
    )
  )
)

(defun zb:explode-region-curves (reg / ret objs out o typ sub)
  ;; Explode a REGION. Se alguma versao do AutoCAD devolver outra REGION
  ;; intermediaria, explode recursivamente ate chegar em curvas.
  (setq ret (vl-catch-all-apply 'vla-Explode (list reg)))
  (if (vl-catch-all-error-p ret)
    nil
    (progn
      (setq objs (zb:variant-objects ret)
            out '())
      (foreach o objs
        (setq typ
          (vl-catch-all-apply 'vla-get-ObjectName (list o)))
        (if (and (not (vl-catch-all-error-p typ))
                 (= typ "AcDbRegion"))
          (progn
            (setq sub (zb:explode-region-curves o))
            (zb:safe-delete o)
            (if sub
              (setq out (append out sub))
            )
          )
          (setq out (append out (list o)))
        )
      )
      out
    )
  )
)

(defun zb:split-region-cycles (reg / curves cycles tolList tol done)
  ;; Retorna LISTA DE LOOPS; cada loop e uma lista de curvas VLA.
  ;; Faz tentativas de tolerancia crescentes apenas na identificacao
  ;; topologica. As geometrias em si nao sao alteradas.
  (setq curves (zb:explode-region-curves reg))
  (if (null curves)
    nil
    (progn
      (setq tolList (list 1e-8 1e-7 1e-6 1e-5 1e-4)
            cycles nil
            done nil)
      (while (and tolList (not done))
        (setq tol (car tolList)
              tolList (cdr tolList)
              cycles (zb:extract-cycles curves tol))
        (if cycles (setq done T))
      )

      (if done
        cycles
        (progn
          (zb:delete-list curves)
          nil
        )
      )
    )
  )
)

(defun c:ZEBRA (/ *error* acad doc space ent obj typ elev gap thick border
                  ang angdeg pitch u n mmN mmU nmin nmax tmin tmax margin
                  t0 t1 d g1 g2 pts gapPl gapReg cutReg boolErr
                  hatch keepRegs count layerObj undoStarted innerPl innerReg
                  areaCut failed parts part partArea partCount splitCount directCount
                  gapIndex failIndex failStage splitTol oldNomutt
                  loopGroups grp auxCurves)

  (setq acad (vlax-get-acad-object)
        doc  (vla-get-ActiveDocument acad)
        space (if (= 1 (getvar "CVPORT"))
                (vla-get-PaperSpace doc)
                (vla-get-ModelSpace doc))
        undoStarted nil
        keepRegs '()
        hatch nil
        innerPl nil
        innerReg nil
        gapPl nil
        gapReg nil
        cutReg nil
        failed nil
        failIndex nil
        failStage nil
        gapIndex 0
        oldNomutt (getvar "NOMUTT"))

  (defun *error* (msg)
    (zb:safe-delete gapPl)
    (zb:safe-delete gapReg)
    (zb:safe-delete cutReg)
    (zb:safe-delete innerReg)
    (zb:safe-delete innerPl)
    (zb:delete-list keepRegs)
    (if hatch (zb:safe-delete hatch))
    (if oldNomutt (setvar "NOMUTT" oldNomutt))
    (if undoStarted
      (vl-catch-all-apply 'vla-EndUndoMark (list doc)))
    (if (and msg
             (/= msg "Function cancelled")
             (/= msg "quit / exit abort"))
      (princ (strcat "\nErro ZEBRA: " msg)))
    (princ))

  (princ "\nZEBRA v1.5 - contorno + faixas em UMA unica HATCH SOLID.")

  (setq ent (car (entsel "\nSelecione a LWPOLYLINE fechada de contorno: ")))

  (cond
    ((null ent)
      (princ "\nNada selecionado."))

    (T
      (setq obj (vlax-ename->vla-object ent)
            typ (vla-get-ObjectName obj))

      (cond
        ((/= typ "AcDbPolyline")
          (princ "\nSelecione uma LWPOLYLINE 2D fechada."))

        ((/= :vlax-true (vla-get-Closed obj))
          (princ "\nA polilinha precisa estar fechada."))

        (T
          (initget 7)
          (setq gap (getdist "\nEspacamento LIVRE entre as faixas: "))

          (initget 7)
          (setq thick (getdist "\nEspessura das faixas: "))

          (initget 6)
          (setq border
            (getdist
              (strcat
                "\nEspessura do contorno <"
                (rtos thick 2 3)
                ">: ")))
          (if (null border) (setq border thick))

          (initget 6)
          (setq angdeg (getreal "\nAngulo das faixas em graus <45>: "))
          (if (null angdeg) (setq angdeg 45.0))

          (setq pitch (+ gap thick)
                ang (* pi (/ angdeg 180.0))
                u (list (cos ang) (sin ang))
                n (list (- (sin ang)) (cos ang))
                elev (if (vlax-property-available-p obj 'Elevation)
                       (vla-get-Elevation obj)
                       0.0))

          (vla-StartUndoMark doc)
          (setq undoStarted T)

          (setq layerObj (zb:ensure-layer doc *zb-layer*))

          ;; Silencia mensagens repetitivas do motor de HATCH durante a montagem.
          (setvar "NOMUTT" 1)

          ;; A decomposicao especial usa tolerancias adaptativas internamente.
          (setq splitTol 1e-7)

          ;; O offset interno define onde TERMINA o contorno.
          (setq innerPl (zb:inward-offset obj border))

          (if (null innerPl)
            (progn
              (setvar "NOMUTT" oldNomutt)
              (vla-EndUndoMark doc)
              (setq undoStarted nil)
              (princ
                "\nNao foi possivel gerar o offset interno. Reduza a espessura do contorno."))

            (progn
              ;; Regiao interna somente para recortar os VAOS.
              (setq innerReg (zb:region-from-object space innerPl))

              (if (null innerReg)
                (progn
                  (zb:safe-delete innerPl)
                  (setq innerPl nil)
                  (setvar "NOMUTT" oldNomutt)
                  (vla-EndUndoMark doc)
                  (setq undoStarted nil)
                  (princ "\nFalha ao criar a regiao interna do contorno."))

                (progn
                  ;; A HATCH comeca preenchendo TODO o contorno original.
                  ;; Depois removemos os espacos livres por INNER LOOPS.
                  (setq hatch (zb:new-solid-hatch space *zb-layer*))

                  (if (not (zb:append-outer hatch obj))
                    (progn
                      (zb:safe-delete hatch)
                      (setq hatch nil)
                      (zb:safe-delete innerReg)
                      (setq innerReg nil)
                      (zb:safe-delete innerPl)
                      (setq innerPl nil)
                      (setvar "NOMUTT" oldNomutt)
                      (vla-EndUndoMark doc)
                      (setq undoStarted nil)
                      (princ "\nFalha ao adicionar o contorno externo a hachura."))

                    (progn
                      ;; Range do padrao calculado sobre o limite interno.
                      (setq mmN (zb:minmax-proj-curve (vlax-vla-object->ename innerPl) n)
                            mmU (zb:minmax-proj-bbox innerPl u)
                            nmin (car mmN)
                            nmax (cadr mmN)
                            tmin (car mmU)
                            tmax (cadr mmU)
                            margin (+ pitch (- tmax tmin))
                            t0 (- tmin margin)
                            t1 (+ tmax margin))

                      ;; Padrao:
                      ;; primeira faixa tem centro em nmin, mantendo a mesma origem das versoes anteriores.
                      ;; Portanto o primeiro VAO comeca em nmin + thick/2.
                      (setq d (+ nmin (/ thick 2.0))
                            count 0
                            splitCount 0
                            directCount 0)

                      (while (and (< d nmax) (not failed))
                        (setq g1 d
                              g2 (+ d gap))

                        (if (> g2 nmax) (setq g2 nmax))

                        (if (> (- g2 g1) 1e-10)
                          (progn
                            (setq pts
                              (list
                                (zb:pt-from-tu t0 g1 u n)
                                (zb:pt-from-tu t1 g1 u n)
                                (zb:pt-from-tu t1 g2 u n)
                                (zb:pt-from-tu t0 g2 u n)))

                            (setq gapPl (zb:add-closed-lwpoly space pts elev))
                            (setq gapReg (zb:region-from-object space gapPl))
                            (zb:safe-delete gapPl)
                            (setq gapPl nil)

                            (if gapReg
                              (progn
                                ;; cutReg = regiao interna INTERSECT vao.
                                (setq cutReg (vla-Copy innerReg))
                                (setq boolErr
                                  (vl-catch-all-apply
                                    'vla-Boolean
                                    (list cutReg acIntersection gapReg)))

                                (if (vl-catch-all-error-p boolErr)
                                  (setq failed T
                                        failIndex gapIndex
                                        failStage "BOOLEAN INTERSECTION")
                                  (progn
                                    (setq areaCut (zb:closed-area cutReg))
                                    (if (and areaCut (> areaCut 1e-10))
                                      (progn
                                        ;; CASO COMUM: o recorte e uma unica ilha.
                                        ;; Tenta adicionar a REGION diretamente, sem EXPLODE.
                                        (if (zb:append-inner hatch cutReg)
                                          (progn
                                            (setq keepRegs (cons cutReg keepRegs)
                                                  cutReg nil
                                                  count (1+ count)
                                                  directCount (1+ directCount))
                                          )

                                          ;; CASO ESPECIAL: a REGION pode conter varios loops,
                                          ;; inclusive loops que apenas ENCOSTAM num vertice.
                                          ;; A v1.5 extrai ciclos fechados independentes e
                                          ;; passa as curvas diretamente ao AppendInnerLoop.
                                          (progn
                                            (setq loopGroups
                                              (zb:split-region-cycles cutReg))

                                            (if loopGroups
                                              (progn
                                                (zb:safe-delete cutReg)
                                                (setq cutReg nil
                                                      partCount 0)

                                                (foreach grp loopGroups
                                                  (if (not failed)
                                                    (if (zb:append-inner-objs hatch grp)
                                                      (progn
                                                        ;; As curvas precisam existir ate Evaluate.
                                                        (setq keepRegs
                                                          (append grp keepRegs))
                                                        (setq partCount (1+ partCount)
                                                              splitCount (1+ splitCount))
                                                      )
                                                      (progn
                                                        ;; Se falhou, remove inclusive os loops
                                                        ;; ainda nao anexados.
                                                        (setq failed T
                                                              failIndex gapIndex
                                                              failStage
                                                               "APPEND INNER DOS CICLOS")
                                                      )
                                                    )
                                                  )
                                                )

                                                ;; Se um grupo falhou, os demais objetos
                                                ;; explodidos ainda precisam ser limpos.
                                                (if failed
                                                  (progn
                                                    (foreach grp loopGroups
                                                      (foreach part grp
                                                        (if (not (member part keepRegs))
                                                          (zb:safe-delete part)
                                                        )
                                                      )
                                                    )
                                                  )
                                                  (if (> partCount 0)
                                                    (setq count (1+ count))
                                                  )
                                                )
                                              )
                                              (setq failed T
                                                    failIndex gapIndex
                                                    failStage
                                                     "DECOMPOSICAO EM CICLOS")
                                            )
                                          )
                                        )
                                      )
                                    )
                                  )
                                )

                                (zb:safe-delete cutReg)
                                (setq cutReg nil)
                                (zb:safe-delete gapReg)
                                (setq gapReg nil)
                              )
                            )
                          )
                        )

                        ;; proximo vao apos uma faixa de espessura THICK
                        (setq d (+ d gap thick)
                              gapIndex (1+ gapIndex))
                      )

                      (if failed
                        (progn
                          (zb:safe-delete hatch)
                          (setq hatch nil)
                          (setvar "NOMUTT" oldNomutt)
                          (princ
                            (strcat
                              "\nFalha no vao #" (itoa (if failIndex failIndex gapIndex))
                              (if failStage
                                (strcat " | etapa: " failStage)
                                "")
                              "."))
                          (princ
                            "\nA geometria auxiliar foi removida; o contorno original permanece intacto."))

                        (progn
                          ;; UMA unica avaliacao => UMA entidade HATCH.
                          (setq boolErr
                            (vl-catch-all-apply 'vla-Evaluate (list hatch)))

                          (if (vl-catch-all-error-p boolErr)
                            (progn
                              (zb:safe-delete hatch)
                              (setq hatch nil)
                              (princ "\nFalha ao avaliar a hachura final."))
                            (progn
                              (vla-put-Layer hatch *zb-layer*)
                              (if (vlax-property-available-p hatch 'Color T)
                                (vla-put-Color hatch 256))
                            )
                          )
                        )
                      )

                      ;; Auxiliares so sao apagados DEPOIS do Evaluate.
                      (zb:delete-list keepRegs)
                      (setq keepRegs '())
                      (zb:safe-delete innerReg)
                      (setq innerReg nil)
                      (zb:safe-delete innerPl)
                      (setq innerPl nil)

                      (setvar "NOMUTT" oldNomutt)
                      (vla-Regen doc :vlax-true)
                      (vla-EndUndoMark doc)
                      (setq undoStarted nil)

                      (if hatch
                        (princ
                          (strcat
                            "\nZEBRA concluido: 1 HATCH no layer "
                            *zb-layer*
                            " | contorno=" (rtos border 2 3)
                            " | faixa=" (rtos thick 2 3)
                            " | vao=" (rtos gap 2 3)
                            " | angulo=" (rtos angdeg 2 2)
                            " graus | vaos recortados=" (itoa count)
                            " | diretos=" (itoa directCount)
                            " | loops via ciclos=" (itoa splitCount)
                            "."))
                      )
                    )
                  )
                )
              )
            )
          )
        )
      )
    )
  )
  (princ)
)

(princ "\nZEBRA v1.5 carregado. Comando: ZEBRA")
(princ)
