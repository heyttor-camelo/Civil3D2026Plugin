;;@TITULO  Diagnostico de hachura (v3.0)
;;@CATEGORIA Hachuras
;;@COMANDO ANALISARHATCH
;;@COMANDO ANALISARHATCHCLEAR
;;@COMANDO ANALISARHATCHHELP
;;@VERSAO  3.0
;;@DESC    Investiga uma hachura que aparece no desenho mas cuja area fica vazia ou da erro.
;;@DESC    Gera copias temporarias dos contornos e procura segmento de comprimento zero, segmento minusculo,
;;@DESC    auto-intersecao, sobreposicao, loop que nao vira REGION e loops que se cruzam ou se tocam.
;;@DESC    Deixa so circulos de marcacao na layer ANALISAR-HATCH. Versao anterior do HATCHDIAG31.
;;@USO     Digite ANALISARHATCH e selecione a hachura
;;@USO     Leia o relatorio e veja as marcas coloridas
;;@USO     ANALISARHATCHCLEAR apaga as marcas
;; ----------------------------------------------------------------
(vl-load-com)

;; =============================================================================
;; ANALISARHATCH v3.0 - Civil 3D / AutoCAD 2026
;;
;; OBJETIVO
;; Diagnosticar uma HATCH que existe e e exibida, mas cuja propriedade AREA
;; aparece vazia ou falha ao ser consultada.
;;
;; POR QUE ESTA ROTINA ANALISA A HATCH, E NAO APENAS UMA POLILINHA
;; Uma HATCH pode ter muitos loops/ilhas. Cada contorno isoladamente pode ser
;; fechado e aparentemente correto, mas a HATCH ainda pode ser invalida por:
;;   - self-intersection dentro de um loop;
;;   - segmento zero-length ou extremamente curto;
;;   - trechos sobrepostos/duplicados;
;;   - dois loops diferentes se cruzando ou apenas se tocando;
;;   - loop que o kernel do AutoCAD nao consegue converter em REGION;
;;   - corrupcao/inconsistencia da propria definicao interna da HATCH.
;;
;; ESTRATEGIA
;; 1) Seleciona a propria HATCH.
;; 2) Tenta ler Area e NumberOfLoops.
;; 3) Usa HATCHGENERATEBOUNDARY para gerar COPIAS temporarias dos loops reais
;;    armazenados na HATCH. Essas copias sao apagadas no final.
;; 4) Explode uma copia de cada boundary para linhas/arcos temporarios.
;; 5) Procura zero-length, tiny segments, self-intersection e overlap.
;; 6) Testa cada boundary com REGION (kernel geometrico do AutoCAD).
;; 7) Cruza os segmentos de boundaries DIFERENTES para achar toque/cruzamento.
;; 8) Deixa apenas circulos de diagnostico no layer ANALISAR-HATCH.
;;
;; CORES DAS MARCAS
;;   1 Vermelho = zero-length / boundary nao fechada
;;   2 Amarelo  = segmento extremamente pequeno
;;   3 Verde    = boundary que falhou no teste REGION
;;   4 Cyan     = dois boundaries diferentes se tocam/cruzam/sobrepoem
;;   6 Magenta  = self-intersection/overlap dentro do mesmo boundary
;;
;; COMANDOS
;;   ANALISARHATCH       -> diagnostica uma HATCH completa
;;   ANALISARHATCHCLEAR  -> apaga somente as marcas da rotina
;;   ANALISARHATCHHELP   -> explica criterios e correcoes recomendadas
;;
;; IMPORTANTE
;; A rotina NAO corrige automaticamente a geometria. Em contornos de projeto,
;; especialmente com arcos, uma correcao automatica pode modificar a geometria
;; correta. O objetivo aqui e localizar o ponto/loop defeituoso com seguranca.
;; =============================================================================

(defun AH3:pt3 (p)
  (cond
    ((and p (= (length p) 3)) p)
    ((and p (= (length p) 2)) (list (car p) (cadr p) 0.0))
    (T (list 0.0 0.0 0.0))
  )
)

(defun AH3:safe-sa-list (sa / r)
  ;; IntersectWith pode devolver SAFEARRAY vazio. Chamar safearray->list
  ;; diretamente nesse caso e uma das causas de "Indice invalido".
  (if sa
    (progn
      (setq r (vl-catch-all-apply 'vlax-safearray->list (list sa)))
      (if (vl-catch-all-error-p r) nil r)
    )
    nil
  )
)

(defun AH3:variant-list (x / y r)
  (cond
    ((null x) nil)
    ((= (type x) 'VARIANT)
      (setq r (vl-catch-all-apply 'vlax-variant-value (list x)))
      (if (vl-catch-all-error-p r)
        nil
        (progn
          (setq y r)
          (cond
            ((= (type y) 'SAFEARRAY) (AH3:safe-sa-list y))
            ((listp y) y)
            (T nil)
          )
        )
      )
    )
    ((= (type x) 'SAFEARRAY) (AH3:safe-sa-list x))
    ((listp x) x)
    (T nil)
  )
)

(defun AH3:triples (lst / out)
  (while (>= (length lst) 3)
    (setq out (cons (list (car lst) (cadr lst) (caddr lst)) out))
    (setq lst (cdddr lst))
  )
  (reverse out)
)

(defun AH3:safe-delete (obj)
  (if (and obj (= (type obj) 'VLA-OBJECT))
    (vl-catch-all-apply 'vla-Delete (list obj))
  )
)

(defun AH3:get-layer (doc / lays r lay)
  (setq lays (vla-get-Layers doc))
  (setq r (vl-catch-all-apply 'vla-Item (list lays "ANALISAR-HATCH")))
  (if (vl-catch-all-error-p r)
    (setq lay (vla-Add lays "ANALISAR-HATCH"))
    (setq lay r)
  )
  lay
)

(defun AH3:mark (space pt rad color / c)
  (setq c
    (vl-catch-all-apply
      'vla-AddCircle
      (list space (vlax-3d-point (AH3:pt3 pt)) rad)
    )
  )
  (if (not (vl-catch-all-error-p c))
    (progn
      (vla-put-Layer c "ANALISAR-HATCH")
      (vla-put-Color c color)
      c
    )
    nil
  )
)

(defun AH3:pt-eq (a b tol)
  (and a b (<= (distance (AH3:pt3 a) (AH3:pt3 b)) tol))
)

(defun AH3:uniq-pt (p lst tol / q found)
  (setq found nil)
  (foreach q lst
    (if (AH3:pt-eq p q tol) (setq found T))
  )
  (if found lst (cons (AH3:pt3 p) lst))
)

(defun AH3:bbox (obj / mn mx r a b)
  (setq r
    (vl-catch-all-apply
      '(lambda (o) (vla-GetBoundingBox o 'mn 'mx))
      (list obj)
    )
  )
  (if (vl-catch-all-error-p r)
    nil
    (progn
      (setq a (AH3:variant-list mn)
            b (AH3:variant-list mx))
      (if (and a b) (list (AH3:pt3 a) (AH3:pt3 b)) nil)
    )
  )
)

(defun AH3:bbox-overlap-p (b1 b2 tol / a1 a2 c1 c2)
  (if (and b1 b2)
    (progn
      (setq a1 (car b1) a2 (cadr b1)
            c1 (car b2) c2 (cadr b2))
      (and
        (<= (- (car a1) tol) (+ (car c2) tol))
        (<= (- (car c1) tol) (+ (car a2) tol))
        (<= (- (cadr a1) tol) (+ (cadr c2) tol))
        (<= (- (cadr c1) tol) (+ (cadr a2) tol))
      )
    )
    T
  )
)

(defun AH3:bbox-center (b)
  (if b
    (list
      (/ (+ (car (car b)) (car (cadr b))) 2.0)
      (/ (+ (cadr (car b)) (cadr (cadr b))) 2.0)
      (/ (+ (caddr (car b)) (caddr (cadr b))) 2.0)
    )
    '(0.0 0.0 0.0)
  )
)

(defun AH3:curve-length (obj / ep r)
  (setq ep (vl-catch-all-apply 'vlax-curve-getEndParam (list obj)))
  (if (or (vl-catch-all-error-p ep) (null ep))
    nil
    (progn
      (setq r (vl-catch-all-apply 'vlax-curve-getDistAtParam (list obj ep)))
      (if (vl-catch-all-error-p r) nil r)
    )
  )
)

(defun AH3:point-at-frac (obj f / len r)
  (setq len (AH3:curve-length obj))
  (if (and len (> len 0.0))
    (progn
      (setq r
        (vl-catch-all-apply
          'vlax-curve-getPointAtDist
          (list obj (* len f))
        )
      )
      (if (vl-catch-all-error-p r) nil (AH3:pt3 r))
    )
    nil
  )
)

(defun AH3:curve-mid (obj)
  (AH3:point-at-frac obj 0.5)
)

(defun AH3:endpoint-p (obj p tol / s e)
  (setq s (vl-catch-all-apply 'vlax-curve-getStartPoint (list obj))
        e (vl-catch-all-apply 'vlax-curve-getEndPoint   (list obj)))
  (or
    (and (not (vl-catch-all-error-p s)) (AH3:pt-eq s p tol))
    (and (not (vl-catch-all-error-p e)) (AH3:pt-eq e p tol))
  )
)

(defun AH3:point-on-curve-p (p obj tol / cp)
  (setq cp
    (vl-catch-all-apply
      'vlax-curve-getClosestPointTo
      (list obj (AH3:pt3 p))
    )
  )
  (and
    (not (vl-catch-all-error-p cp))
    cp
    (<= (distance (AH3:pt3 p) (AH3:pt3 cp)) tol)
  )
)

(defun AH3:intersections (a b / r lst)
  ;; 0 = acExtendNone. Numero usado de proposito para nao depender da constante.
  (setq r (vl-catch-all-apply 'vla-IntersectWith (list a b 0)))
  (if (vl-catch-all-error-p r)
    nil
    (progn
      (setq lst (AH3:variant-list r))
      (AH3:triples lst)
    )
  )
)

(defun AH3:line-overlap-point (a b tol / p1 p2 q1 q2 vx vy wx wy cross len2 t1 t2 lo hi tm)
  (if (and
        (= (vla-get-ObjectName a) "AcDbLine")
        (= (vla-get-ObjectName b) "AcDbLine")
      )
    (progn
      (setq p1 (AH3:pt3 (vlax-curve-getStartPoint a))
            p2 (AH3:pt3 (vlax-curve-getEndPoint a))
            q1 (AH3:pt3 (vlax-curve-getStartPoint b))
            q2 (AH3:pt3 (vlax-curve-getEndPoint b))
            vx (- (car p2) (car p1))
            vy (- (cadr p2) (cadr p1))
            len2 (+ (* vx vx) (* vy vy)))
      (if (> len2 (* tol tol))
        (progn
          ;; Distancia perpendicular em forma de produto vetorial 2D.
          (setq wx (- (car q1) (car p1))
                wy (- (cadr q1) (cadr p1))
                cross (abs (- (* vx wy) (* vy wx))))
          (if (<= cross (* tol (sqrt len2)))
            (progn
              (setq t1 (/ (+ (* (- (car q1) (car p1)) vx)
                              (* (- (cadr q1) (cadr p1)) vy)) len2)
                    t2 (/ (+ (* (- (car q2) (car p1)) vx)
                              (* (- (cadr q2) (cadr p1)) vy)) len2)
                    lo (max 0.0 (min t1 t2))
                    hi (min 1.0 (max t1 t2)))
              (if (> (- hi lo) (/ tol (sqrt len2)))
                (progn
                  (setq tm (/ (+ lo hi) 2.0))
                  (list (+ (car p1) (* vx tm))
                        (+ (cadr p1) (* vy tm))
                        (caddr p1))
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

(defun AH3:arc-overlap-point (a b tol / c1 c2 r1 r2 samples p hit)
  ;; Detecta overlap de arcos que pertencem ao mesmo circulo.
  ;; Amostra pontos INTERIORES (25/50/75%) para nao confundir simples toque
  ;; de extremidades com sobreposicao.
  (if (and
        (= (vla-get-ObjectName a) "AcDbArc")
        (= (vla-get-ObjectName b) "AcDbArc")
      )
    (progn
      (setq c1 (AH3:variant-list (vla-get-Center a))
            c2 (AH3:variant-list (vla-get-Center b))
            r1 (vla-get-Radius a)
            r2 (vla-get-Radius b))
      (if (and c1 c2
               (<= (distance (AH3:pt3 c1) (AH3:pt3 c2)) tol)
               (<= (abs (- r1 r2)) tol))
        (progn
          (setq samples
            (list
              (AH3:point-at-frac a 0.25)
              (AH3:point-at-frac a 0.50)
              (AH3:point-at-frac a 0.75)
              (AH3:point-at-frac b 0.25)
              (AH3:point-at-frac b 0.50)
              (AH3:point-at-frac b 0.75)
            )
          )
          (setq hit nil)
          (foreach p samples
            (if (and p (null hit)
                     (or (AH3:point-on-curve-p p a tol)
                         (AH3:point-on-curve-p p b tol)))
              ;; Precisamos saber se o ponto pertence AO OUTRO arco.
              (cond
                ((and (AH3:point-on-curve-p p a tol)
                      (AH3:point-on-curve-p p b tol))
                  (setq hit p)
                )
              )
            )
          )
          hit
        )
        nil
      )
    )
    nil
  )
)

(defun AH3:overlap-point (a b tol / p)
  (setq p (AH3:line-overlap-point a b tol))
  (if p p (AH3:arc-overlap-point a b tol))
)

(defun AH3:explode-copy (obj / cp r lst)
  (setq cp (vl-catch-all-apply 'vla-Copy (list obj)))
  (if (vl-catch-all-error-p cp)
    nil
    (progn
      (setq r (vl-catch-all-apply 'vla-Explode (list cp)))
      (AH3:safe-delete cp)
      (if (vl-catch-all-error-p r)
        nil
        (progn
          (setq lst (AH3:variant-list r))
          lst
        )
      )
    )
  )
)

(defun AH3:segment-data (obj / segs out s idx len)
  (setq segs (AH3:explode-copy obj)
        out nil
        idx 0)
  (foreach s segs
    (setq len (AH3:curve-length s))
    (setq out
      (cons
        (list
          s
          (AH3:bbox s)
          len
          (AH3:curve-mid s)
          idx
        )
        out
      )
    )
    (setq idx (1+ idx))
  )
  (reverse out)
)

(defun AH3:delete-segdata (sd / rec)
  (foreach rec sd
    (AH3:safe-delete (car rec))
  )
)

(defun AH3:adjacent-p (i j n closed)
  (or
    (= j (1+ i))
    (and closed (= i 0) (= j (1- n)))
  )
)

(defun AH3:make-object-array (objs / sa i)
  (if objs
    (progn
      (setq sa
        (vlax-make-safearray
          vlax-vbObject
          (cons 0 (1- (length objs)))
        )
      )
      (setq i 0)
      (foreach o objs
        (vlax-safearray-put-element sa i o)
        (setq i (1+ i))
      )
      sa
    )
    nil
  )
)

(defun AH3:region-test (obj space / cp sa r regs reg area total)
  ;; Retorno: (OK quantidade area mensagem)
  (setq cp (vl-catch-all-apply 'vla-Copy (list obj)))
  (if (vl-catch-all-error-p cp)
    (list nil 0 nil "Nao foi possivel copiar boundary")
    (progn
      (setq sa (AH3:make-object-array (list cp)))
      (setq r (vl-catch-all-apply 'vla-AddRegion (list space sa)))
      (if (vl-catch-all-error-p r)
        (progn
          (AH3:safe-delete cp)
          (list nil 0 nil (vl-catch-all-error-message r))
        )
        (progn
          (setq regs (AH3:variant-list r)
                total 0.0)
          (foreach reg regs
            (setq area (vl-catch-all-apply 'vla-get-Area (list reg)))
            (if (not (vl-catch-all-error-p area))
              (setq total (+ total area))
            )
          )
          (foreach reg regs (AH3:safe-delete reg))
          ;; AddRegion normalmente consome/deleta a curva copiada; safe-delete
          ;; torna a limpeza idempotente caso ela ainda exista.
          (AH3:safe-delete cp)
          (list T (length regs) total nil)
        )
      )
    )
  )
)

(defun AH3:entities-after (before / e out)
  (setq e (if before (entnext before) (entnext)))
  (while e
    (setq out (cons e out))
    (setq e (entnext e))
  )
  (reverse out)
)

(defun AH3:poly-ename-p (e / typ)
  (if e
    (progn
      (setq typ (cdr (assoc 0 (entget e))))
      (member typ '("LWPOLYLINE" "POLYLINE"))
    )
    nil
  )
)

(defun AH3:closed-p (obj / r)
  (if (vlax-property-available-p obj 'Closed)
    (progn
      (setq r (vl-catch-all-apply 'vla-get-Closed (list obj)))
      (and (not (vl-catch-all-error-p r)) (= r :vlax-true))
    )
    nil
  )
)

(defun AH3:area (obj / r)
  (setq r (vl-catch-all-apply 'vla-get-Area (list obj)))
  (if (vl-catch-all-error-p r) nil r)
)

(defun AH3:loop-source-summary (hatch count / i loopObjs r lst o handles)
  ;; Apenas informativo. Se a HATCH for associativa e GetLoopAt estiver
  ;; disponivel, mostra quantidade de objetos e handles por loop.
  (setq i 0)
  (while (< i count)
    (setq loopObjs nil)
    (setq r
      (vl-catch-all-apply
        '(lambda (hh ii) (vla-GetLoopAt hh ii 'loopObjs))
        (list hatch i)
      )
    )
    (if (vl-catch-all-error-p r)
      (princ
        (strcat "\n  Loop interno " (itoa i)
                ": GetLoopAt falhou -> "
                (vl-catch-all-error-message r)))
      (progn
        (setq lst (AH3:variant-list loopObjs)
              handles "")
        (foreach o lst
          (if (= (type o) 'VLA-OBJECT)
            (setq handles
              (strcat handles
                (if (= handles "") "" ",")
                (vla-get-Handle o)))
          )
        )
        (princ
          (strcat "\n  Loop interno " (itoa i)
                  ": objetos=" (itoa (length lst))
                  (if (/= handles "")
                    (strcat " | handles=" handles)
                    "")))
      )
    )
    (setq i (1+ i))
  )
)

(defun c:ANALISARHATCH
  (/ *error* acad doc space ent hatch objName tol tinyTol markRad
     areaRes hatchArea loopCount assocRes before newEnts boundaries e obj
     records rec idx closed bbox area sd n i j r1 r2 s1 s2 b1 b2 len mid
     zeroCount tinyCount selfPts selfOverlapPts crossPts crossOverlapPts
     regionRes regionOK regionCount regionArea regionMsg regionFailCount
     badBoundaryCount totalSegments op ips ip adjacent
     recA recB sdA sdB bbA bbB aidx bidx allTempSegData
     oldCmdecho genResult generatedCount)

  (vl-load-com)

  (setq acad (vlax-get-acad-object)
        doc  (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        allTempSegData nil
        boundaries nil)

  (AH3:get-layer doc)

  (defun *error* (msg)
    (foreach rec allTempSegData
      (AH3:delete-segdata rec)
    )
    (foreach e boundaries
      (if (and e (entget e)) (entdel e))
    )
    (if oldCmdecho (setvar "CMDECHO" oldCmdecho))
    (if (and msg
             (/= msg "Function cancelled")
             (/= msg "quit / exit abort"))
      (princ (strcat "\n[ERRO] " msg))
    )
    (princ)
  )

  (princ "\n--- ANALISARHATCH v3.0 | DIAGNOSTICO COMPLETO DA HATCH ---")
  (setq ent (car (entsel "\nSelecione a HATCH que esta sem AREA: ")))

  (if (null ent)
    (progn (princ "\nNada selecionado.") (princ))
    (progn
      (setq hatch (vlax-ename->vla-object ent)
            objName (vla-get-ObjectName hatch))

      (if (/= objName "AcDbHatch")
        (progn
          (princ (strcat "\nObjeto selecionado: " objName))
          (princ "\nSelecione a propria HATCH, nao a polilinha.")
          (princ)
        )
        (progn
          (setq tol (getreal "\nTolerancia geometrica <0.0001>: "))
          (if (or (null tol) (<= tol 0.0)) (setq tol 0.0001))

          (setq markRad (getreal "\nRaio das marcas <0.50>: "))
          (if (or (null markRad) (<= markRad 0.0)) (setq markRad 0.50))

          ;; Segmento menor que 10x a tolerancia = aviso de tiny segment.
          (setq tinyTol (* tol 10.0)
                zeroCount 0
                tinyCount 0
                selfPts nil
                selfOverlapPts nil
                crossPts nil
                crossOverlapPts nil
                regionFailCount 0
                badBoundaryCount 0
                totalSegments 0)

          ;; ---------------------------------------------------------------
          ;; Informacoes da HATCH
          ;; ---------------------------------------------------------------
          (setq areaRes (vl-catch-all-apply 'vla-get-Area (list hatch)))
          (if (vl-catch-all-error-p areaRes)
            (progn
              (setq hatchArea nil)
              (princ
                (strcat "\nAREA da HATCH: FALHOU -> "
                        (vl-catch-all-error-message areaRes)))
            )
            (progn
              (setq hatchArea areaRes)
              (princ (strcat "\nAREA da HATCH: " (rtos hatchArea 2 8)))
            )
          )

          (setq loopCount
            (vl-catch-all-apply 'vla-get-NumberOfLoops (list hatch)))
          (if (vl-catch-all-error-p loopCount)
            (setq loopCount 0)
          )
          (princ (strcat "\nLoops declarados pela HATCH: " (itoa loopCount)))

          (if (vlax-property-available-p hatch 'AssociativeHatch)
            (progn
              (setq assocRes
                (vl-catch-all-apply 'vla-get-AssociativeHatch (list hatch)))
              (if (not (vl-catch-all-error-p assocRes))
                (princ
                  (strcat "\nAssociativa: "
                          (if (= assocRes :vlax-true) "SIM" "NAO")))
              )
            )
          )

          ;; Mostra handles das geometrias-fonte quando GetLoopAt permitir.
          (if (> loopCount 0)
            (progn
              (princ "\n\nObjetos-fonte informados pela HATCH:")
              (AH3:loop-source-summary hatch loopCount)
            )
          )

          ;; ---------------------------------------------------------------
          ;; Gera boundaries temporarios a partir DA PROPRIA HATCH.
          ;; Assim analisamos exatamente os loops armazenados nela.
          ;; ---------------------------------------------------------------
          (setq before (entlast)
                oldCmdecho (getvar "CMDECHO"))
          (setvar "CMDECHO" 0)

          (setq genResult
            (vl-catch-all-apply
              '(lambda () (vl-cmdf "_.HATCHGENERATEBOUNDARY" ent ""))
              nil
            )
          )

          (setvar "CMDECHO" oldCmdecho)
          (setq oldCmdecho nil)

          (if (vl-catch-all-error-p genResult)
            (princ
              (strcat "\n\n[CRITICO] HATCHGENERATEBOUNDARY falhou: "
                      (vl-catch-all-error-message genResult)))
          )

          (setq newEnts (AH3:entities-after before)
                boundaries nil)
          (foreach e newEnts
            (if (AH3:poly-ename-p e)
              (setq boundaries (cons e boundaries))
            )
          )
          (setq boundaries (reverse boundaries)
                generatedCount (length boundaries))

          (princ
            (strcat "\nBoundaries temporarios gerados: "
                    (itoa generatedCount)))

          (if (= generatedCount 0)
            (progn
              (princ "\n[CRITICO] A propria HATCH nao conseguiu gerar seus boundaries.")
              (princ "\nIsto aponta para definicao interna corrompida/invalida da HATCH.")
              (princ "\nUse HATCHEDIT > Recreate Boundary ou recrie a HATCH a partir dos contornos.")
            )
            (progn
              ;; -----------------------------------------------------------
              ;; Analise individual de cada boundary
              ;; Record = (indice ename obj bbox closed area segData)
              ;; -----------------------------------------------------------
              (setq records nil idx 1)
              (foreach e boundaries
                (setq obj    (vlax-ename->vla-object e)
                      bbox   (AH3:bbox obj)
                      closed (AH3:closed-p obj)
                      area   (AH3:area obj)
                      sd     (AH3:segment-data obj)
                      n      (length sd)
                      totalSegments (+ totalSegments n))

                ;; Guarda todos os segmentos temporarios para limpeza segura.
                (setq allTempSegData (cons sd allTempSegData))

                (princ
                  (strcat "\n\nBoundary " (itoa idx)
                          " | segmentos=" (itoa n)
                          " | closed=" (if closed "SIM" "NAO")
                          (if area (strcat " | area=" (rtos area 2 6)) "")))

                (if (not closed)
                  (progn
                    (setq badBoundaryCount (1+ badBoundaryCount))
                    (AH3:mark space (AH3:bbox-center bbox) (* markRad 1.4) 1)
                    (princ "\n  [CRITICO] Boundary nao esta fechado.")
                  )
                )

                ;; Zero/tiny por comprimento real do segmento explodido.
                (foreach rec sd
                  (setq len (nth 2 rec)
                        mid (nth 3 rec))
                  (if len
                    (cond
                      ((<= len tol)
                        (setq zeroCount (1+ zeroCount))
                        (if mid (AH3:mark space mid markRad 1))
                        (princ
                          (strcat "\n  [CRITICO] ZERO-LENGTH segmento "
                                  (itoa (nth 4 rec))
                                  " | L=" (rtos len 2 10)))
                      )
                      ((<= len tinyTol)
                        (setq tinyCount (1+ tinyCount))
                        (if mid (AH3:mark space mid (* markRad 0.8) 2))
                        (princ
                          (strcat "\n  [AVISO] Tiny segment "
                                  (itoa (nth 4 rec))
                                  " | L=" (rtos len 2 10)))
                      )
                    )
                  )
                )

                ;; Self-intersection / overlap dentro deste boundary.
                (setq i 0)
                (while (< i n)
                  (setq j (1+ i))
                  (while (< j n)
                    (setq r1 (nth i sd)
                          r2 (nth j sd)
                          s1 (car r1)
                          s2 (car r2)
                          b1 (cadr r1)
                          b2 (cadr r2)
                          adjacent (AH3:adjacent-p i j n closed))

                    (if (AH3:bbox-overlap-p b1 b2 tol)
                      (progn
                        ;; Mesmo segmentos vizinhos podem voltar sobre si
                        ;; proprios. Overlap nunca e considerado legitimo.
                        (setq op (AH3:overlap-point s1 s2 tol))
                        (if op
                          (progn
                            (setq selfOverlapPts
                              (AH3:uniq-pt op selfOverlapPts tol))
                            (AH3:mark space op (* markRad 1.45) 6)
                            (princ
                              (strcat "\n  [CRITICO] OVERLAP interno: segmentos "
                                      (itoa i) " e " (itoa j)))
                          )
                        )

                        ;; Para segmentos vizinhos, ignora SOMENTE o ponto
                        ;; compartilhado normal. Qualquer outra intersecao fica.
                        (setq ips (AH3:intersections s1 s2))
                        (foreach ip ips
                          (if
                            (not
                              (and adjacent
                                   (AH3:endpoint-p s1 ip tol)
                                   (AH3:endpoint-p s2 ip tol)))
                            (progn
                              (setq selfPts (AH3:uniq-pt ip selfPts tol))
                              (AH3:mark space ip (* markRad 1.25) 6)
                              (princ
                                (strcat "\n  [CRITICO] SELF-INTERSECT: segmentos "
                                        (itoa i) " e " (itoa j)
                                        " em " (vl-prin1-to-string ip)))
                            )
                          )
                        )
                      )
                    )
                    (setq j (1+ j))
                  )
                  (setq i (1+ i))
                )

                ;; REGION test do boundary inteiro.
                (setq regionRes (AH3:region-test obj space)
                      regionOK (car regionRes)
                      regionCount (cadr regionRes)
                      regionArea (caddr regionRes)
                      regionMsg (cadddr regionRes))

                (if (and regionOK (= regionCount 1))
                  (princ
                    (strcat "\n  REGION: OK | area="
                            (rtos regionArea 2 8)))
                  (progn
                    (setq regionFailCount (1+ regionFailCount)
                          badBoundaryCount (1+ badBoundaryCount))
                    (AH3:mark space (AH3:bbox-center bbox) (* markRad 1.7) 3)
                    (princ
                      (strcat "\n  [CRITICO] REGION FALHOU/SUSPEITA"
                              (if regionMsg (strcat " -> " regionMsg) "")
                              (if regionOK
                                (strcat " | regioes=" (itoa regionCount))
                                "")))
                  )
                )

                (setq records
                  (cons (list idx e obj bbox closed area sd) records))
                (setq idx (1+ idx))
              )
              (setq records (reverse records))

              ;; -----------------------------------------------------------
              ;; Intersecoes ENTRE boundaries diferentes.
              ;; Qualquer toque/cruzamento de loops distintos e suspeito.
              ;; Nested islands corretas nao se intersectam.
              ;; -----------------------------------------------------------
              (princ "\n\n--- TESTE ENTRE BOUNDARIES ---")
              (setq aidx 0)
              (while (< aidx (length records))
                (setq bidx (1+ aidx))
                (while (< bidx (length records))
                  (setq recA (nth aidx records)
                        recB (nth bidx records)
                        bbA  (nth 3 recA)
                        bbB  (nth 3 recB))

                  (if (AH3:bbox-overlap-p bbA bbB tol)
                    (progn
                      (setq sdA (nth 6 recA)
                            sdB (nth 6 recB))
                      (foreach r1 sdA
                        (foreach r2 sdB
                          (if (AH3:bbox-overlap-p (cadr r1) (cadr r2) tol)
                            (progn
                              (setq s1 (car r1)
                                    s2 (car r2))

                              (setq op (AH3:overlap-point s1 s2 tol))
                              (if op
                                (progn
                                  (setq crossOverlapPts
                                    (AH3:uniq-pt op crossOverlapPts tol))
                                  (AH3:mark space op (* markRad 1.55) 4)
                                  (princ
                                    (strcat "\n[CRITICO] Boundary "
                                            (itoa (car recA)) " e "
                                            (itoa (car recB))
                                            " possuem TRECHO SOBREPOSTO."))
                                )
                              )

                              (setq ips (AH3:intersections s1 s2))
                              (foreach ip ips
                                (setq crossPts
                                  (AH3:uniq-pt ip crossPts tol))
                                (AH3:mark space ip (* markRad 1.35) 4)
                                (princ
                                  (strcat "\n[CRITICO] Boundary "
                                          (itoa (car recA)) " toca/cruza Boundary "
                                          (itoa (car recB))
                                          " em " (vl-prin1-to-string ip)))
                              )
                            )
                          )
                        )
                      )
                    )
                  )
                  (setq bidx (1+ bidx))
                )
                (setq aidx (1+ aidx))
              )

              ;; -----------------------------------------------------------
              ;; Resumo
              ;; -----------------------------------------------------------
              (princ "\n\n========================================================")
              (princ "\n RESUMO - ANALISARHATCH v3.0")
              (princ "\n========================================================")
              (princ (strcat "\nLoops declarados pela HATCH       : " (itoa loopCount)))
              (princ (strcat "\nBoundaries analisados             : " (itoa generatedCount)))
              (princ (strcat "\nSegmentos analisados              : " (itoa totalSegments)))
              (princ (strcat "\nZero-length                       : " (itoa zeroCount)))
              (princ (strcat "\nTiny segments                     : " (itoa tinyCount)))
              (princ (strcat "\nSelf-intersections                : " (itoa (length selfPts))))
              (princ (strcat "\nOverlaps internos                 : " (itoa (length selfOverlapPts))))
              (princ (strcat "\nToques/cruzamentos entre loops    : " (itoa (length crossPts))))
              (princ (strcat "\nOverlaps entre loops              : " (itoa (length crossOverlapPts))))
              (princ (strcat "\nFalhas no teste REGION            : " (itoa regionFailCount)))
              (princ "\n========================================================")

              (cond
                ((or (> zeroCount 0)
                     (> (length selfPts) 0)
                     (> (length selfOverlapPts) 0)
                     (> (length crossPts) 0)
                     (> (length crossOverlapPts) 0)
                     (> regionFailCount 0))
                  (princ "\nRESULTADO: FOI ENCONTRADA GEOMETRIA CAPAZ DE INVALIDAR A AREA.")
                  (princ "\nVa ate as marcas e corrija somente os pontos indicados.")
                )
                ((> tinyCount 0)
                  (princ "\nRESULTADO: somente segmentos muito pequenos foram encontrados.")
                  (princ "\nEles sao suspeitos. Corrija-os primeiro e regenere/recrie a HATCH.")
                )
                ((null hatchArea)
                  (princ "\nRESULTADO: boundaries passaram nos testes, mas a HATCH continua sem AREA.")
                  (princ "\nProvavel inconsistencia/corrupcao da propria HATCH.")
                  (princ "\nRecrie o boundary e depois recrie a HATCH.")
                )
                (T
                  (princ "\nRESULTADO: nenhum defeito geometrico foi localizado.")
                  (princ "\nA HATCH e seus boundaries passaram nos testes executados.")
                )
              )

              (princ "\n\nCORES:")
              (princ "\n vermelho = zero-length/aberto")
              (princ "\n amarelo  = tiny segment")
              (princ "\n magenta  = self-intersect/overlap dentro de um loop")
              (princ "\n cyan     = dois loops diferentes se tocam/cruzam/sobrepoem")
              (princ "\n verde    = boundary que nao virou uma REGION valida")
            )
          )

          ;; Limpa segmentos temporarios do EXPLODE.
          (foreach rec allTempSegData
            (AH3:delete-segdata rec)
          )
          (setq allTempSegData nil)

          ;; Apaga boundaries gerados pelo HATCHGENERATEBOUNDARY.
          (foreach e boundaries
            (if (and e (entget e)) (entdel e))
          )
          (setq boundaries nil)

          (vla-Regen doc 1)
          (princ "\n\nUse ANALISARHATCHCLEAR para apagar as marcas.")
          (princ "\nUse ANALISARHATCHHELP para ver como corrigir cada tipo de erro.\n")
          (princ)
        )
      )
    )
  )
)

(defun c:ANALISARHATCHCLEAR (/ ss)
  (if (setq ss (ssget "_X" '((8 . "ANALISAR-HATCH"))))
    (progn
      (command "_.ERASE" ss "")
      (princ "\nMarcas do ANALISARHATCH apagadas.")
    )
    (princ "\nNenhuma marca do ANALISARHATCH encontrada.")
  )
  (princ)
)

(defun c:ANALISARHATCHHELP ()
  (princ "\n\nANALISARHATCH v3.0 - HELP")
  (princ "\n========================================================")
  (princ "\nO QUE O COMANDO FAZ")
  (princ "\n Analisa a propria HATCH e todos os loops/ilhas armazenados nela.")
  (princ "\n Nao altera os contornos originais e nao corrige nada automaticamente.")
  (princ "\n")
  (princ "\nCOMANDOS")
  (princ "\n ANALISARHATCH      - diagnostico completo.")
  (princ "\n ANALISARHATCHCLEAR - apaga circulos de diagnostico.")
  (princ "\n ANALISARHATCHHELP  - mostra esta ajuda.")
  (princ "\n")
  (princ "\nCOMO CORRIGIR")
  (princ "\n VERMELHO - ZERO-LENGTH / ABERTO")
  (princ "\n   Remova o vertice/segmento duplicado. Em polilinha, edite o vertice")
  (princ "\n   localmente; evite fechar repetindo manualmente o primeiro ponto.")
  (princ "\n")
  (princ "\n AMARELO - TINY SEGMENT")
  (princ "\n   Segmento muito curto. Nem sempre e fatal, mas e forte suspeito.")
  (princ "\n   Reconstrua apenas o pequeno trecho ou elimine o vertice excedente.")
  (princ "\n")
  (princ "\n MAGENTA - SELF-INTERSECT / OVERLAP INTERNO")
  (princ "\n   O mesmo contorno cruza ou sobrepoe a si proprio.")
  (princ "\n   Use TRIM/BREAK/PEDIT no ponto marcado; depois JOIN/CLOSE se necessario.")
  (princ "\n")
  (princ "\n CYAN - PROBLEMA ENTRE DOIS LOOPS")
  (princ "\n   Duas ilhas/contornos se tocam, cruzam ou possuem trecho coincidente.")
  (princ "\n   Uma ilha deve ficar totalmente dentro/fora da outra sem tocar a borda.")
  (princ "\n   Ajuste/trim um dos dois contornos no ponto marcado.")
  (princ "\n")
  (princ "\n VERDE - REGION FALHOU")
  (princ "\n   O kernel do AutoCAD nao aceitou aquele boundary como face fechada.")
  (princ "\n   Se nao houver outra marca no mesmo loop, recrie esse boundary com")
  (princ "\n   BOUNDARY/HATCHGENERATEBOUNDARY e substitua o contorno defeituoso.")
  (princ "\n")
  (princ "\nSE TUDO PASSAR E A AREA CONTINUAR VAZIA")
  (princ "\n   Provavel inconsistencia do objeto HATCH. Gere/recrie os boundaries")
  (princ "\n   e crie uma HATCH nova. Nao precisa redesenhar tudo manualmente.")
  (princ "\n========================================================\n")
  (princ)
)

(princ "\nANALISARHATCH v3.0 carregado.")
(princ "\nComandos: ANALISARHATCH, ANALISARHATCHCLEAR, ANALISARHATCHHELP.")
(princ)
