;;@TITULO  Quantificar hachuras por estaca
;;@CATEGORIA Hachuras
;;@ICONE hachura
;;@COMANDO QHPE
;;@COMANDO QHPEGERAR
;;@COMANDO QHPEUPDATE
;;@COMANDO QHPERESUMO
;;@COMANDO QHPEEXPORT
;;@COMANDO QHPEEXPORTTODAS
;;@COMANDO QHPENOMEVIA
;;@COMANDO QHPEAJUDA
;;@VERSAO  3.5
;;@DESC    Divide uma polilinha-eixo em intervalos de estaca e calcula a area de cada hachura dentro de cada
;;@DESC    intervalo, agrupando pelo layer e considerando ilhas. Gera tabela no desenho e exporta CSV/Excel,
;;@DESC    com nome da via e exportacao de varias vias de uma vez.
;;@USO     Digite QHPE (no Model)
;;@USO     Selecione a polilinha-eixo e as hachuras
;;@USO     Informe a estaca inicial e o intervalo
;;@USO     QHPENOMEVIA da nome a via; QHPEEXPORTTODAS exporta todas; QHPEAJUDA lista o resto
;; ----------------------------------------------------------------
;;; ================================================================
;;; QHPE - Quantificar Hachuras Por Estaca
;;; Versao 2.8 consolidada - AutoCAD / Civil 3D para Windows
;;;
;;; Comando: QHPE
;;;
;;; Funcao:
;;;   - Seleciona uma polilinha aberta como eixo;
;;;   - Seleciona hachuras;
;;;   - Divide a polilinha em intervalos de estacas;
;;;   - Calcula a area de cada hachura dentro de cada intervalo;
;;;   - Agrupa o resultado pelo layer das hachuras;
;;;   - Considera ilhas internas conforme o estilo da hachura;
;;;   - Gera tabela no desenho e/ou arquivo CSV.
;;;
;;; Observacoes:
;;;   - Executar no Model Space.
;;;   - Polilinha e hachuras devem ser coplanares, normalmente Z = 0.
;;;   - A estaca informada corresponde ao inicio geometrico da polilinha.
;;; ;;;   - Hachuras sobrepostas no mesmo layer sao somadas separadamente.
;;; ================================================================

(vl-load-com)

(setq qhpe:*version* "2.8")
(setq qhpe:*temp-layer-name* "_QHPE_TEMP")
(setq qhpe:*temp-objects* nil)
(setq qhpe:*interval-default* 20.0)
(setq qhpe:*width-default* 50.0)
(setq qhpe:*linear-factor-default* 1.0)
(setq qhpe:*text-height-default* nil)

;;; Constantes ActiveX
(setq qhpe:acUnion 0)
(setq qhpe:acIntersection 1)
(setq qhpe:acSubtraction 2)
(setq qhpe:acTitleRow 1)
(setq qhpe:acHeaderRow 2)
(setq qhpe:acDataRow 4)

(defun qhpe:safe-delete (obj)
  (if obj
    (vl-catch-all-apply 'vla-Delete (list obj))
  )
  nil
)

(defun qhpe:safe-area (obj / r)
  (if obj
    (progn
      (setq r (vl-catch-all-apply 'vla-get-Area (list obj)))
      (if (vl-catch-all-error-p r) nil r)
    )
  )
)

(defun qhpe:safe-copy (obj / r)
  (setq r (vl-catch-all-apply 'vla-Copy (list obj)))
  (if (vl-catch-all-error-p r) nil r)
)

(defun qhpe:safe-vla-object (ent / r)
  (if ent
    (progn
      (setq r (vl-catch-all-apply 'vlax-ename->vla-object (list ent)))
      (if (vl-catch-all-error-p r) nil r)
    )
  )
)

;;; Retorna T somente quando o objeto ActiveX ainda existe no desenho.
;;; Alguns Boolean/Explode do AutoCAD podem invalidar objetos temporarios.
(defun qhpe:object-alive-p (obj / r)
  (if obj
    (progn
      (setq r (vl-catch-all-apply 'vla-get-ObjectName (list obj)))
      (not (vl-catch-all-error-p r))
    )
    nil
  )
)

(defun qhpe:safe-curve-point-at-dist (curve distance / r)
  (setq r
    (vl-catch-all-apply
      'vlax-curve-getPointAtDist
      (list curve distance)
    )
  )
  (if (vl-catch-all-error-p r) nil r)
)

(defun qhpe:safe-boolean (obj operation other / r)
  (setq r (vl-catch-all-apply 'vla-Boolean (list obj operation other)))
  (not (vl-catch-all-error-p r))
)

(defun qhpe:array->list (value)
  (cond
    ((= (type value) 'VARIANT)
     (vlax-safearray->list (vlax-variant-value value))
    )
    ((= (type value) 'SAFEARRAY)
     (vlax-safearray->list value)
    )
    (T value)
  )
)

(defun qhpe:bbox (obj / mn mx r)
  (setq r (vl-catch-all-apply 'vla-GetBoundingBox (list obj 'mn 'mx)))
  (if (not (vl-catch-all-error-p r))
    (list (qhpe:array->list mn) (qhpe:array->list mx))
  )
)

(defun qhpe:bbox-overlap-p (b1 b2 tol / a1 a2 c1 c2)
  (if (and b1 b2)
    (progn
      (setq a1 (car b1)
            a2 (cadr b1)
            c1 (car b2)
            c2 (cadr b2))
      (and
        (<= (- (car a1) tol) (+ (car c2) tol))
        (>= (+ (car a2) tol) (- (car c1) tol))
        (<= (- (cadr a1) tol) (+ (cadr c2) tol))
        (>= (+ (cadr a2) tol) (- (cadr c1) tol))
      )
    )
  )
)

(defun qhpe:tolerance (a b)
  (max 1.0e-8 (* 1.0e-7 (max 1.0 (abs a) (abs b))))
)

;;; Retorna T quando a regiao SMALL esta integralmente dentro de BIG.
;;; O teste usa a reducao de area de BIG apos subtrair SMALL.
(defun qhpe:region-contained-p (small big / as ab cb cs after delta tol ok)
  (setq as (qhpe:safe-area small)
        ab (qhpe:safe-area big)
        ok nil)
  (if (and as ab (> as 0.0) (<= as (+ ab (qhpe:tolerance as ab))))
    (progn
      (setq cb (qhpe:safe-copy big)
            cs (qhpe:safe-copy small))
      (if (and cb cs (qhpe:safe-boolean cb qhpe:acSubtraction cs))
        (progn
          (setq after (qhpe:safe-area cb))
          (if (null after) (setq after 0.0))
          (setq delta (- ab after)
                tol (qhpe:tolerance as ab))
          (setq ok (<= (abs (- delta as)) tol))
        )
      )
      (qhpe:safe-delete cb)
      (qhpe:safe-delete cs)
    )
  )
  ok
)

;;; Area de intersecao entre duas regioes.
;;; A verificacao de contencao evita o comportamento do Boolean quando
;;; duas regioes nao se interceptam e o primeiro objeto permanece inalterado.
(defun qhpe:intersection-area (reg-a bbox-a reg-b bbox-b / aa ca cb ar tol result)
  (setq result 0.0)
  (if (qhpe:bbox-overlap-p bbox-a bbox-b 1.0e-7)
    (progn
      (setq aa (qhpe:safe-area reg-a)
            ca (qhpe:safe-copy reg-a)
            cb (qhpe:safe-copy reg-b))
      (if (and aa ca cb (qhpe:safe-boolean ca qhpe:acIntersection cb))
        (progn
          (setq ar (qhpe:safe-area ca))
          (if ar
            (progn
              (setq tol (qhpe:tolerance aa ar))
              (if (<= (abs (- ar aa)) tol)
                (if (qhpe:region-contained-p reg-a reg-b)
                  (setq result aa)
                  (setq result 0.0)
                )
                (setq result (max 0.0 ar))
              )
            )
            (if (qhpe:region-contained-p reg-a reg-b)
              (setq result aa)
            )
          )
        )
      )
      (qhpe:safe-delete ca)
      (qhpe:safe-delete cb)
    )
  )
  result
)

(defun qhpe:get-layer-object (doc name / lays obj r)
  (setq lays (vla-get-Layers doc)
        r (vl-catch-all-apply 'vla-Item (list lays name)))
  (if (vl-catch-all-error-p r)
    (setq obj (vla-Add lays name))
    (setq obj r)
  )
  obj
)

(defun qhpe:prepare-temp-layer (doc / lay)
  (setq lay (qhpe:get-layer-object doc qhpe:*temp-layer-name*))
  (vl-catch-all-apply 'vla-put-LayerOn (list lay :vlax-true))
  (vl-catch-all-apply 'vla-put-Freeze (list lay :vlax-false))
  (vl-catch-all-apply 'vla-put-Lock (list lay :vlax-false))
  lay
)

(defun qhpe:cleanup-temp ()
  (foreach obj qhpe:*temp-objects*
    (qhpe:safe-delete obj)
  )
  (setq qhpe:*temp-objects* nil)
  (princ)
)

(defun qhpe:register-temp (obj)
  (if obj
    (setq qhpe:*temp-objects* (cons obj qhpe:*temp-objects*))
  )
  obj
)

(defun qhpe:safearray-objects (objects / arr i)
  (setq arr (vlax-make-safearray vlax-vbObject (cons 0 (1- (length objects))))
        i 0)
  (foreach obj objects
    (vlax-safearray-put-element arr i obj)
    (setq i (1+ i))
  )
  arr
)

(defun qhpe:add-region-from-object (space obj / result regs reg)
  (setq result
    (vl-catch-all-apply
      'vla-AddRegion
      (list space (qhpe:safearray-objects (list obj)))
    )
  )
  (if (not (vl-catch-all-error-p result))
    (progn
      (setq regs (qhpe:array->list result))
      (if regs
        (progn
          (setq reg (car regs))
          (qhpe:register-temp reg)
          reg
        )
      )
    )
  )
)

(defun qhpe:entities-after (marker / e result)
  (setq e (entnext marker))
  (while e
    (setq result (cons e result)
          e (entnext e))
  )
  (reverse result)
)

;;; Gera os contornos temporarios de uma hachura e os converte em regioes.
;;; Cada item retornado contem (REGIAO CONTORNO). O contorno original e
;;; preservado para localizar inicios/finais no diagrama sem precisar
;;; explodir regioes, operacao que em algumas versoes gera "Object was erased".
(defun qhpe:hatch-regions (hatch space / marker cmd-result entities pairs obj reg typ data)
  (setq marker (entlast))
  (setq cmd-result
    (vl-catch-all-apply
      'vl-cmdf
      (list "_.HATCHGENERATEBOUNDARY" hatch "")
    )
  )
  (if (not (vl-catch-all-error-p cmd-result))
    (progn
      (setq entities (qhpe:entities-after marker))
      (foreach ent entities
        (setq data (entget ent)
              typ (if data (cdr (assoc 0 data))))
        (if (member typ '("LWPOLYLINE" "POLYLINE" "CIRCLE" "ELLIPSE" "SPLINE"))
          (progn
            (setq obj (qhpe:safe-vla-object ent))
            (if obj
              (progn
                (qhpe:register-temp obj)
                (setq reg (qhpe:add-region-from-object space obj))
                (if reg
                  (setq pairs (cons (list reg obj) pairs))
                  (qhpe:safe-delete obj)
                )
              )
            )
          )
          (if (setq obj (qhpe:safe-vla-object ent))
            (qhpe:safe-delete obj)
          )
        )
      )
    )
  )
  (reverse pairs)
)

(defun qhpe:region-depth (reg regs / depth area-r area-o handle-r handle-o)
  (setq depth 0
        area-r (qhpe:safe-area reg)
        handle-r (vla-get-Handle reg))
  (foreach other regs
    (setq handle-o (vla-get-Handle other))
    (if (/= handle-r handle-o)
      (progn
        (setq area-o (qhpe:safe-area other))
        (if (and area-r area-o (> area-o (+ area-r (qhpe:tolerance area-r area-o))))
          (if (qhpe:region-contained-p reg other)
            (setq depth (1+ depth))
          )
        )
      )
    )
  )
  depth
)

;;; Estilos de hachura:
;;; 0 = Normal: alterna cheio/vazio em cada nivel.
;;; 1 = Outer: considera somente contorno externo e primeira ilha.
;;; 2 = Ignore: ignora todas as ilhas.
(defun qhpe:sign-for-depth (depth hatch-style)
  (cond
    ((= hatch-style 2)
     (if (= depth 0) 1.0 0.0)
    )
    ((= hatch-style 1)
     (cond ((= depth 0) 1.0)
           ((= depth 1) -1.0)
           (T 0.0))
    )
    (T
     (if (= (rem depth 2) 0) 1.0 -1.0)
    )
  )
)

(defun qhpe:make-hatch-records (hatch space / layer style pairs regs result pair reg boundary depth sign box style-r hatch-obj)
  (setq hatch-obj (qhpe:safe-vla-object hatch))
  (if hatch-obj
    (progn
      (setq layer (vla-get-Layer hatch-obj)
            style-r (vl-catch-all-apply 'vla-get-HatchStyle (list hatch-obj)))
      (if (vl-catch-all-error-p style-r)
        (setq style 0)
        (setq style style-r)
      )
      (setq pairs (qhpe:hatch-regions hatch space)
            regs (mapcar 'car pairs))
      (foreach pair pairs
        (setq reg (car pair)
              boundary (cadr pair)
              depth (qhpe:region-depth reg regs)
              sign (qhpe:sign-for-depth depth style)
              box (qhpe:bbox reg))
        (if (/= sign 0.0)
          ;; Indices: 0 layer, 1 sinal, 2 regiao, 3 bbox, 4 contorno.
          (setq result (cons (list layer sign reg box boundary) result))
        )
      )
    )
  )
  (reverse result)
)

(defun qhpe:valid-axis-polyline-p (ent / data typ flags)
  (setq data (entget ent)
        typ (cdr (assoc 0 data))
        flags (cond ((cdr (assoc 70 data))) (0)))
  (cond
    ((not (member typ '("LWPOLYLINE" "POLYLINE")))
     (princ "\nO eixo deve ser uma polilinha.")
     nil
    )
    ((/= 0 (logand flags 1))
     (princ "\nA polilinha do eixo deve ser aberta.")
     nil
    )
    ((and (= typ "POLYLINE") (/= 0 (logand flags 8)))
     (princ "\nPolilinha 3D nao e aceita. Use uma polilinha 2D no plano XY.")
     nil
    )
    (T T)
  )
)

(defun qhpe:curve-length (curve / ep r)
  (setq ep (vl-catch-all-apply 'vlax-curve-getEndParam (list curve)))
  (if (not (vl-catch-all-error-p ep))
    (progn
      (setq r (vl-catch-all-apply 'vlax-curve-getDistAtParam (list curve ep)))
      (if (vl-catch-all-error-p r) nil r)
    )
  )
)

(defun qhpe:curve-point-normal (curve dist total / par pt der mag)
  (if (>= dist (- total 1.0e-9))
    (setq par (vlax-curve-getEndParam curve))
    (setq par (vlax-curve-getParamAtDist curve dist))
  )
  (if par
    (progn
      (setq pt (vlax-curve-getPointAtParam curve par)
            der (vlax-curve-getFirstDeriv curve par)
            mag (sqrt (+ (* (car der) (car der)) (* (cadr der) (cadr der)))))
      (if (> mag 1.0e-12)
        (list
          (list (car pt) (cadr pt) 0.0)
          (list (/ (- (cadr der)) mag) (/ (car der) mag) 0.0)
        )
      )
    )
  )
)

(defun qhpe:point-offset (pt normal value)
  (list
    (+ (car pt) (* (car normal) value))
    (+ (cadr pt) (* (cadr normal) value))
    0.0
  )
)

(defun qhpe:sample-distances (d0 d1 step / d values)
  (setq d d0
        values (list d0))
  (while (< (+ d step) (- d1 1.0e-9))
    (setq d (+ d step)
          values (cons d values))
  )
  (reverse (cons d1 values))
)

(defun qhpe:points->double-array (pts / values arr)
  (setq values
    (apply 'append
      (mapcar '(lambda (p) (list (car p) (cadr p))) pts)
    )
  )
  (setq arr (vlax-make-safearray vlax-vbDouble (cons 0 (1- (length values)))))
  (vlax-safearray-fill arr values)
  arr
)

;;; Cria a faixa entre duas estacas usando amostragem da polilinha.
;;; A largura recebida e a do lado ESQUERDO, considerando o sentido crescente
;;; da polilinha. O lado DIREITO usa qhpe:*right-width-current*; quando essa
;;; variavel nao estiver definida, usa a mesma largura para os dois lados.
(defun qhpe:create-band-region (curve total d0 d1 half-width step space / ds left right pn pline reg right-width)
  (setq right-width
    (if (and (numberp qhpe:*right-width-current*) (> qhpe:*right-width-current* 0.0))
      qhpe:*right-width-current*
      half-width)
        ds (qhpe:sample-distances d0 d1 step))
  (foreach d ds
    (setq pn (qhpe:curve-point-normal curve d total))
    (if pn
      (progn
        (setq left
          (cons (qhpe:point-offset (car pn) (cadr pn) half-width) left)
        )
        (setq right
          (cons (qhpe:point-offset (car pn) (cadr pn) (- right-width)) right)
        )
      )
    )
  )
  (setq left (reverse left))
  ;; RIGHT foi montado em ordem inversa e fecha corretamente a faixa.
  (if (and (> (length left) 1) (> (length right) 1))
    (progn
      (setq pline
        (vla-AddLightWeightPolyline
          space
          (qhpe:points->double-array (append left right))
        )
      )
      (qhpe:register-temp pline)
      (vla-put-Closed pline :vlax-true)
      (vla-put-Layer pline qhpe:*temp-layer-name*)
      (setq reg (qhpe:add-region-from-object space pline))
      reg
    )
  )
)

(defun qhpe:add-area (key value alist / item)
  (if (setq item (assoc key alist))
    (subst (cons key (+ (cdr item) value)) item alist)
    (cons (cons key value) alist)
  )
)

(defun qhpe:unique-layers (records / result layer)
  (foreach rec records
    (setq layer (car rec))
    (if (not (member layer result))
      (setq result (cons layer result))
    )
  )
  ;; ACAD_STRLSORT existe no AutoLISP padrao e evita dependencia de
  ;; VL-STRING-LESSP, que nao esta disponivel em algumas instalacoes.
  (acad_strlsort result)
)

(defun qhpe:parse-station (text interval / pos left right)
  (setq text (vl-string-trim " " text)
        pos (vl-string-search "+" text))
  (if pos
    (progn
      (setq left (substr text 1 pos)
            right (substr text (+ pos 2)))
      (+ (* (atof left) interval) (atof right))
    )
    (atof text)
  )
)

(defun qhpe:pad-left (text char size)
  (while (< (strlen text) size)
    (setq text (strcat char text))
  )
  text
)

(defun qhpe:format-station (value interval / stake rem rem-text tol)
  (setq tol (* interval 1.0e-8)
        stake (fix (/ value interval))
        rem (- value (* stake interval)))
  (if (>= rem (- interval tol))
    (setq stake (1+ stake)
          rem 0.0)
  )
  (if (< (abs rem) tol) (setq rem 0.0))
  (setq rem-text (rtos rem 2 2))
  (if (< rem 10.0)
    (setq rem-text (qhpe:pad-left rem-text "0" 5))
  )
  (strcat (itoa stake) "+" rem-text)
)

(defun qhpe:format-number (value decimals)
  (rtos value 2 decimals)
)

(defun qhpe:decimal-comma (text)
  (vl-string-subst "," "." text)
)

(defun qhpe:string-replace-all (new old text / pos start-pos)
  (setq start-pos 0)
  (while (setq pos (vl-string-search old text start-pos))
    (setq text
      (strcat
        (substr text 1 pos)
        new
        (substr text (+ pos (strlen old) 1))
      )
    )
    (setq start-pos (+ pos (strlen new)))
  )
  text
)

(defun qhpe:csv-escape (text)
  (setq text (vl-princ-to-string text))
  (setq text (qhpe:string-replace-all "\"\"" "\"" text))
  (strcat "\"" text "\"")
)

(defun qhpe:join (items separator / result)
  (if items
    (progn
      (setq result (car items))
      (foreach item (cdr items)
        (setq result (strcat result separator item))
      )
    )
    (setq result "")
  )
  result
)

(defun qhpe:row-area (areas layer)
  (if (assoc layer areas) (cdr (assoc layer areas)) 0.0)
)

(defun qhpe:write-csv (filename rows layers linear-factor / file headers values total)
  (if (setq file (open filename "w"))
    (progn
      (setq headers (append (list "Estaca inicial" "Estaca final" "Comprimento (m)") layers (list "Total (m2)")))
      (write-line (qhpe:join (mapcar 'qhpe:csv-escape headers) ";") file)
      (foreach row rows
        (setq total 0.0
              values
          (list
            (qhpe:csv-escape (nth 0 row))
            (qhpe:csv-escape (nth 1 row))
            (qhpe:decimal-comma (qhpe:format-number (* (nth 2 row) linear-factor) 2))
          )
        )
        (foreach layer layers
          (setq total (+ total (qhpe:row-area (nth 3 row) layer)))
          (setq values
            (append values
              (list
                (qhpe:decimal-comma
                  (qhpe:format-number
                    (* (qhpe:row-area (nth 3 row) layer) linear-factor linear-factor)
                    2
                  )
                )
              )
            )
          )
        )
        (setq values
          (append values
            (list
              (qhpe:decimal-comma
                (qhpe:format-number (* total linear-factor linear-factor) 2)
              )
            )
          )
        )
        (write-line (qhpe:join values ";") file)
      )
      (close file)
      T
    )
  )
)

(defun qhpe:set-table-text (table row col text)
  (vl-catch-all-apply 'vla-SetText (list table row col text))
)

(defun qhpe:create-table (space insertion rows layers linear-factor text-height / nrows ncols table row-index col-index total width)
  (setq nrows (+ 2 (length rows))
        ncols (+ 4 (length layers))
        width (* 14.0 text-height))
  (setq table
    (vla-AddTable
      space
      (vlax-3d-point insertion)
      nrows
      ncols
      (* 2.0 text-height)
      width
    )
  )
  (vl-catch-all-apply 'vla-MergeCells (list table 0 0 0 (1- ncols)))
  (qhpe:set-table-text table 0 0 "QUANTITATIVO DE HACHURAS POR ESTACA")
  (qhpe:set-table-text table 1 0 "ESTACA INICIAL")
  (qhpe:set-table-text table 1 1 "ESTACA FINAL")
  (qhpe:set-table-text table 1 2 "COMPR. (m)")
  (setq col-index 3)
  (foreach layer layers
    (qhpe:set-table-text table 1 col-index layer)
    (setq col-index (1+ col-index))
  )
  (qhpe:set-table-text table 1 col-index "TOTAL (m2)")

  (vl-catch-all-apply 'vla-SetTextHeight (list table qhpe:acTitleRow (* 1.2 text-height)))
  (vl-catch-all-apply 'vla-SetTextHeight (list table qhpe:acHeaderRow text-height))
  (vl-catch-all-apply 'vla-SetTextHeight (list table qhpe:acDataRow text-height))

  (setq row-index 2)
  (foreach row rows
    (qhpe:set-table-text table row-index 0 (nth 0 row))
    (qhpe:set-table-text table row-index 1 (nth 1 row))
    (qhpe:set-table-text table row-index 2 (qhpe:format-number (* (nth 2 row) linear-factor) 2))
    (setq col-index 3
          total 0.0)
    (foreach layer layers
      (setq total (+ total (qhpe:row-area (nth 3 row) layer)))
      (qhpe:set-table-text
        table row-index col-index
        (qhpe:format-number
          (* (qhpe:row-area (nth 3 row) layer) linear-factor linear-factor)
          2
        )
      )
      (setq col-index (1+ col-index))
    )
    (qhpe:set-table-text table row-index col-index
      (qhpe:format-number (* total linear-factor linear-factor) 2)
    )
    (setq row-index (1+ row-index))
  )

  ;; Ajustes de largura. Falhas sao ignoradas para compatibilidade.
  (vl-catch-all-apply 'vla-SetColumnWidth (list table 0 (* 11.0 text-height)))
  (vl-catch-all-apply 'vla-SetColumnWidth (list table 1 (* 11.0 text-height)))
  (vl-catch-all-apply 'vla-SetColumnWidth (list table 2 (* 9.0 text-height)))
  (setq col-index 3)
  (repeat (length layers)
    (vl-catch-all-apply 'vla-SetColumnWidth (list table col-index (* 13.0 text-height)))
    (setq col-index (1+ col-index))
  )
  (vl-catch-all-apply 'vla-SetColumnWidth (list table col-index (* 10.0 text-height)))
  table
)

(defun qhpe:default-csv-name (/ name)
  (setq name (vl-filename-base (getvar "DWGNAME")))
  (strcat (getvar "DWGPREFIX") name "_hachuras_por_estaca.csv")
)

(defun qhpe:prompt-real-default (message default / value)
  (initget 6)
  (setq value (getreal (strcat message " <" (rtos default 2 3) ">: ")))
  (if value value default)
)

(defun qhpe:process-intervals (curve total records layers start-station interval half-width linear-factor space / d0 d1 segment-length first-length station-rem step band band-box areas area rows idx rec layer sign reg box)
  (setq d0 0.0
        step (min 2.0 (max 0.20 (/ interval 10.0)))
        idx 0
        station-rem (- start-station (* (fix (/ start-station interval)) interval)))
  (if (< (abs station-rem) (* interval 1.0e-8))
    (setq first-length interval)
    (setq first-length (- interval station-rem))
  )
  (setq segment-length first-length)

  (while (< d0 (- total 1.0e-8))
    (setq d1 (min total (+ d0 segment-length))
          band (qhpe:create-band-region curve total d0 d1 half-width step space)
          areas nil)
    (if band
      (progn
        (setq band-box (qhpe:bbox band))
        (foreach rec records
          (setq layer (nth 0 rec)
                sign  (nth 1 rec)
                reg   (nth 2 rec)
                box   (nth 3 rec))
          (if (qhpe:bbox-overlap-p box band-box 1.0e-7)
            (progn
              (setq area (qhpe:intersection-area reg box band band-box))
              (if (> area 1.0e-10)
                (setq areas (qhpe:add-area layer (* sign area) areas))
              )
            )
          )
        )
        ;; Elimina a faixa logo apos o calculo para reduzir o peso temporario.
        (qhpe:safe-delete band)
      )
    )
    ;; Evita residuos numericos negativos muito pequenos.
    (foreach layer layers
      (if (and (assoc layer areas) (< (cdr (assoc layer areas)) 0.0)
               (> (cdr (assoc layer areas)) -1.0e-7))
        (setq areas (subst (cons layer 0.0) (assoc layer areas) areas))
      )
    )
    (setq rows
      (cons
        (list
          (qhpe:format-station (+ start-station d0) interval)
          (qhpe:format-station (+ start-station d1) interval)
          (- d1 d0)
          areas
          d0
          d1
        )
        rows
      )
    )
    (setq idx (1+ idx))
    (princ (strcat "\rProcessando intervalo " (itoa idx) "..."))
    (setq d0 d1
          segment-length interval)
  )
  (princ "\n")
  (reverse rows)
)

(defun qhpe:legacy-quantification (/ *error* old-error acad doc space old-cmdecho old-osmode old-clayer temp-layer
                 axis-sel axis curve-name total ss interval width linear-factor station-text start-station
                 output records layers i hatch hatch-records rows insertion text-height csv-name table)

  (setq old-error *error*
        acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        old-cmdecho (getvar "CMDECHO")
        old-osmode (getvar "OSMODE")
        old-clayer (getvar "CLAYER"))

  (defun *error* (msg)
    (if (> (getvar "CMDACTIVE") 0)
      (vl-catch-all-apply 'command (list))
    )
    (qhpe:cleanup-temp)
    (if old-clayer (setvar "CLAYER" old-clayer))
    (if old-cmdecho (setvar "CMDECHO" old-cmdecho))
    (if old-osmode (setvar "OSMODE" old-osmode))
    (vl-catch-all-apply 'vla-EndUndoMark (list doc))
    (if temp-layer
      (vl-catch-all-apply 'vla-Delete (list temp-layer))
    )
    (setq *error* old-error)
    (if (and msg (not (wcmatch (strcase msg) "*CANCEL*,*QUIT*,*EXIT*")))
      (princ (strcat "\nErro QHPE: " msg))
    )
    (princ)
  )

  (if (= (getvar "CVPORT") 1)
    (progn
      (princ "\nExecute o comando QHPE no Model Space, nao no Paper Space.")
      (*error* "Cancelado")
    )
    (progn
      (vla-StartUndoMark doc)
      (setvar "CMDECHO" 0)
      (setvar "OSMODE" 0)
      (setq qhpe:*temp-objects* nil)
      (setq temp-layer (qhpe:prepare-temp-layer doc))
      (setvar "CLAYER" qhpe:*temp-layer-name*)

      (princ "\nQHPE - Quantificar hachuras por estaca")
      (setq axis-sel (entsel "\nSelecione a polilinha do eixo: "))

      (cond
        ((null axis-sel)
         (*error* "Cancelado")
        )

        ((not (qhpe:valid-axis-polyline-p (car axis-sel)))
         (*error* "Objeto invalido")
        )

        (T
         (setq axis (car axis-sel)
               total (qhpe:curve-length axis))

         (if (or (null total) (<= total 0.0))
           (*error* "A polilinha selecionada nao possui comprimento valido.")
           (progn
             (setq curve-name (vla-get-ObjectName (vlax-ename->vla-object axis)))
             (princ (strcat "\nObjeto selecionado: " curve-name))
             (princ (strcat "\nComprimento geometrico: " (rtos total 2 3)))
             (princ "\nSelecione as hachuras que devem ser quantificadas:")

             (setq ss (ssget "_:L" '((0 . "HATCH"))))

             (if (null ss)
               (*error* "Nenhuma hachura selecionada.")
               (progn
                 (setq interval
                   (qhpe:prompt-real-default
                     "\nComprimento de cada estaca nas unidades do desenho"
                     qhpe:*interval-default*
                   )
                 )
                 (setq qhpe:*interval-default* interval)

                 (setq station-text (getstring T "\nEstaca no inicio da polilinha <0+0.00>: "))
                 (if (= station-text "") (setq station-text "0+0.00"))
                 (setq start-station (qhpe:parse-station station-text interval))

                 (setq width
                   (qhpe:prompt-real-default
                     "\nLargura de busca para o lado ESQUERDO da polilinha"
                     qhpe:*width-default*
                   )
                 )
                 (setq qhpe:*width-default* width
                       qhpe:*right-width-current*
                         (qhpe:prompt-real-default
                           "\nLargura de busca para o lado DIREITO da polilinha"
                           (if (and (numberp qhpe:*right-width-default*)
                                    (> qhpe:*right-width-default* 0.0))
                             qhpe:*right-width-default* width))
                       qhpe:*right-width-default* qhpe:*right-width-current*)

                 (setq linear-factor
                   (qhpe:prompt-real-default
                     "\nFator linear para converter a unidade do desenho em metros (1 para desenho em metros)"
                     qhpe:*linear-factor-default*
                   )
                 )
                 (setq qhpe:*linear-factor-default* linear-factor)

                 (initget "Tabela CSV Ambos")
                 (setq output (getkword "\nSaida [Tabela/CSV/Ambos] <Ambos>: "))
                 (if (null output) (setq output "Ambos"))

                 (princ "\nGerando limites e identificando ilhas das hachuras...")
                 (setq i 0
                       records nil)
                 (repeat (sslength ss)
                   (setq hatch (ssname ss i)
                         hatch-records (qhpe:make-hatch-records hatch space))
                   (if hatch-records
                     (setq records (append hatch-records records))
                     (princ
                       (strcat
                         "\nAviso: nao foi possivel gerar o limite da hachura "
                         (itoa (1+ i))
                         "."
                       )
                     )
                   )
                   (setq i (1+ i))
                 )

                 (if (null records)
                   (*error* "Nenhuma regiao valida foi criada a partir das hachuras.")
                   (progn
                     (setq layers (qhpe:unique-layers records))
                     (princ (strcat "\nLayers encontrados: " (itoa (length layers))))

                     (setq rows
                       (qhpe:process-intervals
                         axis total records layers start-station interval width linear-factor space
                       )
                     )

                     ;; Retorna ao layer original antes de criar a tabela.
                     (setvar "CLAYER" old-clayer)

                     (if (member output '("Tabela" "Ambos"))
                       (progn
                         (setq insertion (getpoint "\nPonto de insercao da tabela: "))
                         (if insertion
                           (progn
                             (if (null qhpe:*text-height-default*)
                               (setq qhpe:*text-height-default*
                                 (max 0.001 (getvar "TEXTSIZE"))
                               )
                             )
                             (setq text-height
                               (qhpe:prompt-real-default
                                 "\nAltura do texto da tabela"
                                 qhpe:*text-height-default*
                               )
                             )
                             (setq qhpe:*text-height-default* text-height)
                             (setq table
                               (qhpe:create-table
                                 space insertion rows layers linear-factor text-height
                               )
                             )
                             (princ "\nTabela criada.")
                           )
                           (princ "\nCriacao da tabela ignorada.")
                         )
                       )
                     )

                     (if (member output '("CSV" "Ambos"))
                       (progn
                         (setq csv-name
                           (getfiled
                             "Salvar quantitativo em CSV"
                             (qhpe:default-csv-name)
                             "csv"
                             1
                           )
                         )
                         (if csv-name
                           (if (qhpe:write-csv csv-name rows layers linear-factor)
                             (princ (strcat "\nCSV salvo em: " csv-name))
                             (princ "\nNao foi possivel salvar o CSV.")
                           )
                           (princ "\nExportacao CSV ignorada.")
                         )
                       )
                     )

                     (qhpe:cleanup-temp)
                     (setvar "CLAYER" old-clayer)
                     (setvar "CMDECHO" old-cmdecho)
                     (setvar "OSMODE" old-osmode)
                     (vl-catch-all-apply 'vla-EndUndoMark (list doc))
                     (if temp-layer
                       (vl-catch-all-apply 'vla-Delete (list temp-layer))
                     )
                     (setq *error* old-error)
                     (princ
                       (strcat
                         "\nConcluido: "
                         (itoa (length rows))
                         " intervalos processados."
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
  )
  (princ)
)



;;; ================================================================
;;; DIAGRAMA LINEAR
;;; Comando: QHPED
;;;
;;; Gera um diagrama em escala horizontal real, contendo:
;;;   - uma faixa SOLID por layer, ordenada da maior para a menor area;
;;;   - faixas encostadas verticalmente;
;;;   - contorno em polilinha no mesmo layer da hachura;
;;;   - estaqueamento no formato 0+0.00;
;;;   - marcacao exata aproximada dos inicios e finais das ocorrencias;
;;;   - uma caixa por trecho com area do trecho e acumulado por layer;
;;;   - quadros gerados por ENTMAKEX para evitar referencias ActiveX apagadas.
;;; ================================================================

(setq qhpe:*diagram-layer-name* "_QHPE_DIAGRAMA")
(setq qhpe:*diagram-band-height-default* nil)
(setq qhpe:*diagram-text-height-default* nil)

(defun qhpe:format-station-diagram (value interval / stake remainder tol)
  (setq tol (* interval 1.0e-8)
        stake (fix (/ value interval))
        remainder (- value (* stake interval)))
  (if (>= remainder (- interval tol))
    (setq stake (1+ stake)
          remainder 0.0)
  )
  (if (< (abs remainder) tol)
    (setq remainder 0.0)
  )
  (strcat (itoa stake) "+" (rtos remainder 2 2))
)

(defun qhpe:layer-total (rows layer / total)
  (setq total 0.0)
  (foreach row rows
    (setq total (+ total (qhpe:row-area (nth 3 row) layer)))
  )
  total
)

(defun qhpe:sort-layers-by-total (layers rows / pairs)
  (setq pairs
    (mapcar
      '(lambda (layer) (cons layer (qhpe:layer-total rows layer)))
      layers
    )
  )
  (setq pairs
    (vl-sort
      pairs
      '(lambda (a b)
         (if (equal (cdr a) (cdr b) 1.0e-9)
           (< (strcmp (car a) (car b)) 0)
           (> (cdr a) (cdr b))
         )
       )
    )
  )
  (mapcar 'car pairs)
)

(defun qhpe:alist-push (key value alist / item)
  (if (setq item (assoc key alist))
    (subst (cons key (cons value (cdr item))) item alist)
    (cons (cons key (list value)) alist)
  )
)

(defun qhpe:clamp (value minimum maximum)
  (max minimum (min maximum value))
)

(defun qhpe:project-point-to-axis-distance (axis point / closest result)
  (setq closest
    (vl-catch-all-apply
      'vlax-curve-getClosestPointTo
      (list axis point)
    )
  )
  (if (not (vl-catch-all-error-p closest))
    (progn
      (setq result
        (vl-catch-all-apply
          'vlax-curve-getDistAtPoint
          (list axis closest)
        )
      )
      (if (vl-catch-all-error-p result) nil result)
    )
  )
)

(defun qhpe:sample-curve-project (axis curve step / length d point distance minimum maximum)
  (if (qhpe:object-alive-p curve)
    (progn
      (setq length (qhpe:curve-length curve))
      (if (and length (> length 1.0e-9))
        (progn
          (setq d 0.0)
          (while (< d length)
            (setq point (qhpe:safe-curve-point-at-dist curve d))
            (if point
              (progn
                (setq distance (qhpe:project-point-to-axis-distance axis point))
                (if distance
                  (progn
                    (if (or (null minimum) (< distance minimum)) (setq minimum distance))
                    (if (or (null maximum) (> distance maximum)) (setq maximum distance))
                  )
                )
              )
            )
            (setq d (+ d step))
          )
          (setq point (qhpe:safe-curve-point-at-dist curve length))
          (if point
            (progn
              (setq distance (qhpe:project-point-to-axis-distance axis point))
              (if distance
                (progn
                  (if (or (null minimum) (< distance minimum)) (setq minimum distance))
                  (if (or (null maximum) (> distance maximum)) (setq maximum distance))
                )
              )
            )
          )
        )
      )
    )
  )
  (if (and minimum maximum) (list minimum maximum))
)

;;; Projeta diretamente o contorno gerado por HATCHGENERATEBOUNDARY.
;;; Nao usa vla-Explode em Region, evitando referencias COM apagadas.
(defun qhpe:boundary-projection-interval (axis total boundary step / sample-result)
  (if (qhpe:object-alive-p boundary)
    (progn
      (setq sample-result
        (vl-catch-all-apply
          'qhpe:sample-curve-project
          (list axis boundary step)
        )
      )
      (if (and (not (vl-catch-all-error-p sample-result)) sample-result)
        (list
          (qhpe:clamp (car sample-result) 0.0 total)
          (qhpe:clamp (cadr sample-result) 0.0 total)
        )
      )
    )
  )
)

(defun qhpe:projection-intervals-by-layer (axis total records interval / result rec layer sign boundary projected step projection-result)
  (setq step (min 0.50 (max 0.05 (/ interval 100.0))))
  (foreach rec records
    (setq layer (nth 0 rec)
          sign (nth 1 rec)
          boundary (nth 4 rec))
    ;; Apenas contornos positivos geram ocorrencias lineares. As ilhas
    ;; continuam sendo descontadas no quantitativo de area.
    (if (and (> sign 0.0) boundary (qhpe:object-alive-p boundary))
      (progn
        (setq projection-result
          (vl-catch-all-apply
            'qhpe:boundary-projection-interval
            (list axis total boundary step)
          )
        )
        (if (not (vl-catch-all-error-p projection-result))
          (setq projected projection-result)
          (setq projected nil)
        )
        (if (and projected (> (- (cadr projected) (car projected)) 1.0e-7))
          (setq result (qhpe:alist-push layer projected result))
        )
      )
    )
  )
  result
)

(defun qhpe:row-active-intervals (rows layer / result active start finish row area d0 d1)
  (setq active nil)
  (foreach row rows
    (setq area (qhpe:row-area (nth 3 row) layer)
          d0 (nth 4 row)
          d1 (nth 5 row))
    (if (> area 1.0e-8)
      (progn
        (if (not active)
          (setq active T
                start d0)
        )
        (setq finish d1)
      )
      (if active
        (progn
          (setq result (cons (list start finish) result)
                active nil)
        )
      )
    )
  )
  (if active
    (setq result (cons (list start finish) result))
  )
  (reverse result)
)

(defun qhpe:intersect-interval-lists (list-a list-b / result start finish)
  (foreach a list-a
    (foreach b list-b
      (setq start (max (car a) (car b))
            finish (min (cadr a) (cadr b)))
      (if (> (- finish start) 1.0e-7)
        (setq result (cons (list start finish) result))
      )
    )
  )
  (reverse result)
)

(defun qhpe:merge-intervals (intervals tolerance / sorted current result)
  (setq sorted
    (vl-sort intervals '(lambda (a b) (< (car a) (car b))))
  )
  (foreach interval sorted
    (if (null current)
      (setq current interval)
      (if (<= (car interval) (+ (cadr current) tolerance))
        (setq current
          (list (car current) (max (cadr current) (cadr interval)))
        )
        (progn
          (setq result (cons current result)
                current interval)
        )
      )
    )
  )
  (if current (setq result (cons current result)))
  (reverse result)
)

(defun qhpe:diagram-intervals-by-layer (layers rows projected / result coarse exact item)
  (foreach layer layers
    (setq coarse (qhpe:row-active-intervals rows layer)
          item (assoc layer projected)
          exact (if item (cdr item) nil))
    (if exact
      (setq exact (qhpe:intersect-interval-lists coarse exact))
      (setq exact coarse)
    )
    (setq exact (qhpe:merge-intervals exact 0.01))
    (setq result (cons (cons layer exact) result))
  )
  (reverse result)
)

(defun qhpe:add-polyline-object (space points layer closed / object)
  (setq object
    (vla-AddLightWeightPolyline
      space
      (qhpe:points->double-array points)
    )
  )
  (vla-put-Layer object layer)
  (vla-put-Closed object (if closed :vlax-true :vlax-false))
  object
)


(defun qhpe:add-line-object (space point-a point-b layer / object)
  ;; Saida grafica criada por ENTMAKEX. O parametro SPACE e mantido
  ;; apenas para compatibilidade com as chamadas existentes.
  (setq object (qhpe:entmake-line point-a point-b layer))
  object
)

(defun qhpe:add-solid-hatch (space boundary layer / hatch result loop)
  (setq result
    (vl-catch-all-apply
      'vla-AddHatch
      (list space 0 "SOLID" :vlax-false)
    )
  )
  (if (vl-catch-all-error-p result)
    (setq result
      (vl-catch-all-apply
        'vla-AddHatch
        (list space 0 "SOLID" :vlax-false 0)
      )
    )
  )
  (if (not (vl-catch-all-error-p result))
    (progn
      (setq hatch result
            loop (vlax-make-variant (qhpe:safearray-objects (list boundary))))
      (if
        (not
          (vl-catch-all-error-p
            (vl-catch-all-apply 'vla-AppendOuterLoop (list hatch loop))
          )
        )
        (progn
          (vla-put-Layer hatch layer)
          (vl-catch-all-apply 'vla-Evaluate (list hatch))
          hatch
        )
        (progn
          (qhpe:safe-delete hatch)
          nil
        )
      )
    )
  )
)


(defun qhpe:entmake-solid-rectangle (x0 y0 x1 y1 layer / p1 p2 p3 p4)
  ;; A ordem 10,11,12,13 do SOLID e diferente da ordem de contorno:
  ;; o terceiro e o quarto vertices sao invertidos para evitar cruzamento.
  (setq p1 (list x0 y0 0.0)
        p2 (list x1 y0 0.0)
        p3 (list x1 y1 0.0)
        p4 (list x0 y1 0.0))
  (entmakex
    (list
      '(0 . "SOLID")
      '(100 . "AcDbEntity")
      (cons 8 layer)
      '(100 . "AcDbTrace")
      (cons 10 p1)
      (cons 11 p2)
      (cons 12 p4)
      (cons 13 p3)
      '(210 0.0 0.0 1.0)
    )
  )
)

(defun qhpe:create-diagram-band (space x0 x1 y0 y1 layer / fill outline)
  ;; O preenchimento final e um SOLID no layer do servico, com uma
  ;; LWPOLYLINE fechada em volta. Isso elimina o vinculo COM que podia
  ;; invalidar o contorno ao criar HATCH por ActiveX.
  (if (> (- x1 x0) 1.0e-7)
    (progn
      (setq fill (qhpe:entmake-solid-rectangle x0 y0 x1 y1 layer)
            outline (qhpe:entmake-rectangle x0 y0 x1 y1 layer))
      (list fill outline)
    )
  )
)


(defun qhpe:add-mtext-object (space point width height attachment contents layer / object)
  ;; Evita ActiveX na etapa final do diagrama.
  (setq object
    (qhpe:entmake-mtext
      point
      (max width (* 2.0 height))
      height
      attachment
      contents
      layer
    )
  )
  object
)


(defun qhpe:add-rotated-text (space point text height rotation layer / object)
  ;; Texto simples por ENTMAKEX, sem manter referencias COM.
  (setq object
    (entmakex
      (list
        '(0 . "TEXT")
        '(100 . "AcDbEntity")
        (cons 8 layer)
        '(100 . "AcDbText")
        (cons 10 (list (car point) (cadr point) 0.0))
        (cons 40 height)
        (cons 1 text)
        (cons 50 rotation)
        (cons 7 (getvar "TEXTSTYLE"))
        '(72 . 0)
        '(73 . 0)
        '(210 0.0 0.0 1.0)
      )
    )
  )
  object
)

(defun qhpe:rectangle-points (x0 y0 x1 y1)
  (list
    (list x0 y0 0.0)
    (list x1 y0 0.0)
    (list x1 y1 0.0)
    (list x0 y1 0.0)
  )
)

(defun qhpe:main-boundary-distances (rows / result)
  (if rows
    (progn
      (setq result (list (nth 4 (car rows))))
      (foreach row rows
        (setq result (append result (list (nth 5 row))))
      )
    )
  )
  result
)

(defun qhpe:near-any-p (value values tolerance / found)
  (foreach item values
    (if (<= (abs (- value item)) tolerance)
      (setq found T)
    )
  )
  found
)

(defun qhpe:unique-numbers (values tolerance / sorted result previous)
  (setq sorted (vl-sort values '<))
  (foreach value sorted
    (if (or (null previous) (> (abs (- value previous)) tolerance))
      (progn
        (setq result (cons value result)
              previous value)
      )
    )
  )
  (reverse result)
)

(defun qhpe:discontinuity-distances (diagram-intervals main total / values interval)
  (foreach item diagram-intervals
    (foreach interval (cdr item)
      (foreach value interval
        (if (and
              (> value 0.01)
              (< value (- total 0.01))
              (not (qhpe:near-any-p value main 0.02))
            )
          (setq values (cons value values))
        )
      )
    )
  )
  (qhpe:unique-numbers values 0.02)
)

(defun qhpe:update-accumulated (accumulated layer value / item)
  (if (setq item (assoc layer accumulated))
    (subst (cons layer (+ (cdr item) value)) item accumulated)
    (cons (cons layer value) accumulated)
  )
)

(defun qhpe:diagram-box-content
  (row layers accumulated linear-factor start-station interval
    / content layer area accumulated-area active-lines)
  ;; Usa a mesma formatacao 0+0.00 do estaqueamento do diagrama.
  (setq content
    (strcat
      (qhpe:format-station-diagram (+ start-station (nth 4 row)) interval)
      " a "
      (qhpe:format-station-diagram (+ start-station (nth 5 row)) interval)
    )
  )
  (foreach layer layers
    (setq area (qhpe:row-area (nth 3 row) layer)
          accumulated-area (if (assoc layer accumulated) (cdr (assoc layer accumulated)) 0.0))
    (if (> area 1.0e-8)
      (progn
        (setq content
          (strcat
            content
            "\\P"
            layer
            " = "
            (qhpe:format-number (* area linear-factor linear-factor) 2)
            " m2 | Acum. = "
            (qhpe:format-number (* accumulated-area linear-factor linear-factor) 2)
            " m2"
          )
        )
        (setq active-lines (1+ (if active-lines active-lines 0)))
      )
    )
  )
  (if (null active-lines)
    (setq content (strcat content "\\PSEM AREA"))
  )
  (list content (+ 1 (if active-lines active-lines 1)))
)

;;; Os quadros sao criados com ENTMAKEX, e nao com objetos ActiveX.
;;; Isso evita o erro "Automation Error. Object was erased" observado
;;; depois das operacoes booleanas com regioes temporarias.
(defun qhpe:entmake-line (point-a point-b layer / result)
  (setq result
    (entmakex
      (list
        '(0 . "LINE")
        '(100 . "AcDbEntity")
        (cons 8 layer)
        '(100 . "AcDbLine")
        (cons 10 (list (car point-a) (cadr point-a) 0.0))
        (cons 11 (list (car point-b) (cadr point-b) 0.0))
        '(210 0.0 0.0 1.0)
      )
    )
  )
  result
)

(defun qhpe:entmake-rectangle (x0 y0 x1 y1 layer / result)
  (setq result
    (entmakex
      (list
        '(0 . "LWPOLYLINE")
        '(100 . "AcDbEntity")
        (cons 8 layer)
        '(100 . "AcDbPolyline")
        '(90 . 4)
        '(70 . 1)
        (cons 10 (list x0 y0))
        (cons 10 (list x1 y0))
        (cons 10 (list x1 y1))
        (cons 10 (list x0 y1))
        '(210 0.0 0.0 1.0)
      )
    )
  )
  result
)

(defun qhpe:entmake-mtext (point width height attachment contents layer / result)
  (setq result
    (entmakex
      (list
        '(0 . "MTEXT")
        '(100 . "AcDbEntity")
        (cons 8 layer)
        '(100 . "AcDbMText")
        (cons 10 (list (car point) (cadr point) 0.0))
        (cons 40 height)
        (cons 41 width)
        (cons 71 attachment)
        (cons 1 contents)
        (cons 7 (getvar "TEXTSTYLE"))
        '(210 0.0 0.0 1.0)
      )
    )
  )
  result
)


(defun qhpe:create-diagram-box-entmake
  (anchor-x bands-top box-left box-bottom box-right box-top margin text-height contents annotation-layer
    / leader connector rectangle mtext)
  ;; O quadro nasce rente a estaca inicial do trecho. A linha-guia fica
  ;; exatamente nessa estaca e se conecta ao canto inferior esquerdo.
  (setq leader
    (qhpe:entmake-line
      (list anchor-x bands-top 0.0)
      (list anchor-x box-bottom 0.0)
      annotation-layer
    )
  )
  (setq connector
    (qhpe:entmake-line
      (list anchor-x box-bottom 0.0)
      (list box-left box-bottom 0.0)
      annotation-layer
    )
  )
  (setq rectangle
    (qhpe:entmake-rectangle box-left box-bottom box-right box-top annotation-layer)
  )
  (setq mtext
    (qhpe:entmake-mtext
      (list (+ box-left margin) (- box-top margin) 0.0)
      (- (- box-right box-left) (* 2.0 margin))
      text-height
      1
      contents
      annotation-layer
    )
  )
  (and leader connector rectangle mtext)
)


(defun qhpe:create-linear-diagram
  (space insertion rows layers diagram-intervals start-station interval linear-factor band-height text-height annotation-layer
    / x-origin y-origin bands-base bands-top number-layers index layer y0 y1 item interval-data
       main discontinuities distance x label-y line-bottom line-top layer-label-width accumulated row
       d0 d1 segment-length box-width box-left box-right box-bottom box-top box-height box-info contents lines
       margin anchor-x natural-right minimum-width)

  (setq x-origin (car insertion)
        y-origin (cadr insertion)
        number-layers (length layers)
        ;; Reserva inferior para as estacas e para os rotulos verticais
        ;; das descontinuidades.
        bands-base (+ y-origin (max (* 8.0 text-height) (* 0.20 interval)))
        bands-top (+ bands-base (* number-layers band-height))
        label-y (- y-origin (* 0.35 text-height))
        layer-label-width (max (* 1.20 interval) (* 24.0 text-height)))

  (princ "\nDesenhando faixas do diagrama...")

  ;; Faixas: maior area no topo, menor area abaixo.
  (setq index 0)
  (foreach layer layers
    (setq y0 (+ bands-base (* (- number-layers index 1) band-height))
          y1 (+ y0 band-height)
          item (assoc layer diagram-intervals))
    (foreach interval-data (cdr item)
      (qhpe:create-diagram-band
        space
        (+ x-origin (car interval-data))
        (+ x-origin (cadr interval-data))
        y0 y1 layer
      )
    )
    ;; Nome do servico rente ao inicio da faixa.
    (qhpe:add-mtext-object
      space
      (list (- x-origin (* 0.60 text-height)) (+ y0 (/ band-height 2.0)) 0.0)
      layer-label-width
      text-height
      6
      layer
      annotation-layer
    )
    (setq index (1+ index))
  )

  (princ "\nInserindo linhas e textos de estaqueamento...")

  ;; Estacas principais.
  (setq main (qhpe:main-boundary-distances rows)
        line-bottom y-origin
        line-top bands-top)
  (foreach distance main
    (setq x (+ x-origin distance))
    (qhpe:add-line-object
      space
      (list x line-bottom 0.0)
      (list x line-top 0.0)
      annotation-layer
    )
    ;; O texto fica rente e imediatamente a direita da linha da estaca,
    ;; em vez de centralizado no meio do trecho.
    (qhpe:add-mtext-object
      space
      (list (+ x (* 0.22 text-height)) label-y 0.0)
      (max (* 0.80 interval) (* 12.0 text-height))
      text-height
      1
      (qhpe:format-station-diagram (+ start-station distance) interval)
      annotation-layer
    )
  )

  ;; Inicios e finais que nao coincidem com uma estaca principal.
  (setq discontinuities
    (qhpe:discontinuity-distances diagram-intervals main (nth 5 (car (last rows))))
  )
  (foreach distance discontinuities
    (setq x (+ x-origin distance))
    (qhpe:add-line-object
      space
      (list x y-origin 0.0)
      (list x bands-top 0.0)
      annotation-layer
    )
    (qhpe:add-rotated-text
      space
      (list (+ x (* 0.28 text-height)) (+ y-origin (* 0.75 text-height)) 0.0)
      (qhpe:format-station-diagram (+ start-station distance) interval)
      text-height
      (/ pi 2.0)
      annotation-layer
    )
  )

  ;; Uma caixa para cada trecho, ancorada na respectiva estaca inicial.
  ;; O acumulado e atualizado separadamente para cada layer.
  (princ "\nInserindo quadros de areas por trecho...")
  (setq accumulated nil)
  (foreach row rows
    (foreach layer layers
      (setq accumulated
        (qhpe:update-accumulated
          accumulated
          layer
          (qhpe:row-area (nth 3 row) layer)
        )
      )
    )

    (setq d0 (nth 4 row)
          d1 (nth 5 row)
          segment-length (- d1 d0)
          anchor-x (+ x-origin d0)
          margin (* 0.45 text-height)
          box-left (+ anchor-x (* 0.30 text-height))
          natural-right (- (+ x-origin d1) (* 0.30 text-height))
          minimum-width (* 16.0 text-height)
          box-right (max natural-right (+ box-left minimum-width))
          box-info (qhpe:diagram-box-content row layers accumulated linear-factor start-station interval)
          contents (car box-info)
          lines (cadr box-info)
          box-height (+ (* lines 1.55 text-height) (* 2.0 margin))
          box-bottom (+ bands-top (* 2.0 text-height))
          box-top (+ box-bottom box-height))

    ;; A falha de um quadro e tratada sem interromper os seguintes.
    (if
      (not
        (qhpe:create-diagram-box-entmake
          anchor-x bands-top box-left box-bottom box-right box-top
          margin text-height contents annotation-layer
        )
      )
      (princ
        (strcat
          "\nAviso: nao foi possivel criar o quadro do trecho "
          (qhpe:format-station-diagram (+ start-station d0) interval)
          " a "
          (qhpe:format-station-diagram (+ start-station d1) interval)
          "."
        )
      )
    )
  )
  T
)



;;; ================================================================
;;;   - textos das estacas centralizados nas linhas;
;;;   - rotulos exatos de inicio/fim das ocorrencias;
;;;   - quadros por trecho com area e acumulado por layer;
;;;   - etapa grafica sem ActiveX;
;;;   - restauracao segura do layer corrente.
;;; ================================================================

(setq qhpe:*version* "1.7")

(defun qhpe:layer-exists-p (name)
  (and name (tblsearch "LAYER" name))
)

(defun qhpe:safe-set-clayer (name / result)
  (if (not (qhpe:layer-exists-p name))
    (setq name "0")
  )
  (setq result (vl-catch-all-apply 'setvar (list "CLAYER" name)))
  (if (vl-catch-all-error-p result)
    (progn
      (vl-catch-all-apply 'setvar (list "CLAYER" "0"))
      "0"
    )
    name
  )
)

(defun qhpe:entmake-text-justified
  (point text height rotation hjust vjust layer / data)
  (setq data
    (list
      '(0 . "TEXT")
      '(100 . "AcDbEntity")
      (cons 8 layer)
      '(100 . "AcDbText")
      (cons 10 (list (car point) (cadr point) 0.0))
      (cons 11 (list (car point) (cadr point) 0.0))
      (cons 40 height)
      (cons 1 text)
      (cons 50 rotation)
      (cons 7 (getvar "TEXTSTYLE"))
      (cons 72 hjust)
      (cons 73 vjust)
      '(210 0.0 0.0 1.0)
    )
  )
  (entmakex data)
)

(defun qhpe:entmake-fit-text
  (point-a point-b text height layer / result)
  (setq result
    (entmakex
      (list
        '(0 . "TEXT")
        '(100 . "AcDbEntity")
        (cons 8 layer)
        '(100 . "AcDbText")
        (cons 10 (list (car point-a) (cadr point-a) 0.0))
        (cons 11 (list (car point-b) (cadr point-b) 0.0))
        (cons 40 height)
        (cons 1 text)
        (cons 50 0.0)
        (cons 7 (getvar "TEXTSTYLE"))
        '(72 . 5)
        '(73 . 0)
        '(210 0.0 0.0 1.0)
      )
    )
  )
  result
)

(defun qhpe:estimated-text-width (text height)
  (* (max 1 (strlen text)) height 0.62)
)

(defun qhpe:insert-box-text-line
  (x-left x-right y text preferred-height layer / available estimated actual-height)
  (setq available (max 0.01 (- x-right x-left))
        estimated (qhpe:estimated-text-width text preferred-height))
  (if (> estimated available)
    (qhpe:entmake-fit-text
      (list x-left y 0.0)
      (list x-right y 0.0)
      text
      preferred-height
      layer
    )
    (qhpe:entmake-text-justified
      (list x-left y 0.0)
      text
      preferred-height
      0.0
      0
      0
      layer
    )
  )
)

(defun qhpe:row-box-lines
  (row layers accumulated linear-factor start-station interval / result layer area accumulated-area active)
  (setq result
    (list
      (strcat
        (qhpe:format-station-diagram (+ start-station (nth 4 row)) interval)
        " a "
        (qhpe:format-station-diagram (+ start-station (nth 5 row)) interval)
      )
    )
  )
  (foreach layer layers
    (setq area (qhpe:row-area (nth 3 row) layer)
          accumulated-area (if (assoc layer accumulated) (cdr (assoc layer accumulated)) 0.0))
    (if (> area 1.0e-8)
      (progn
        (setq result
          (append
            result
            (list
              (strcat
                layer
                " = "
                (qhpe:format-number (* area linear-factor linear-factor) 2)
                " m2 | Acum. = "
                (qhpe:format-number (* accumulated-area linear-factor linear-factor) 2)
                " m2"
              )
            )
          )
        )
        (setq active T)
      )
    )
  )
  (if (not active)
    (setq result (append result (list "SEM AREA")))
  )
  result
)

(defun qhpe:create-diagram-box-v17
  (anchor-x bands-top box-left box-bottom box-right box-top margin text-height lines annotation-layer
    / line-y text-left text-right line-step first-line)
  (qhpe:entmake-line
    (list anchor-x bands-top 0.0)
    (list anchor-x box-bottom 0.0)
    annotation-layer
  )
  (if (> box-left anchor-x)
    (qhpe:entmake-line
      (list anchor-x box-bottom 0.0)
      (list box-left box-bottom 0.0)
      annotation-layer
    )
  )
  (qhpe:entmake-rectangle box-left box-bottom box-right box-top annotation-layer)

  (setq text-left (+ box-left margin)
        text-right (- box-right margin)
        line-step (* 1.45 text-height)
        line-y (- box-top margin text-height)
        first-line T)
  (foreach text lines
    (qhpe:insert-box-text-line
      text-left text-right line-y text
      (if first-line (* 1.02 text-height) text-height)
      annotation-layer
    )
    (setq first-line nil
          line-y (- line-y line-step))
  )
  T
)

(defun qhpe:diagram-discontinuities-v17
  (diagram-intervals main total / values item occurrence value)
  (foreach item diagram-intervals
    (foreach occurrence (cdr item)
      (foreach value occurrence
        (if (and
              (> value 0.01)
              (< value (- total 0.01))
              (not (qhpe:near-any-p value main 0.02))
            )
          (setq values (cons value values))
        )
      )
    )
  )
  (if values
    (qhpe:unique-numbers values 0.02)
    nil
  )
)

(defun qhpe:create-linear-diagram
  (space insertion rows layers diagram-intervals start-station interval linear-factor band-height text-height annotation-layer
    / x-origin y-origin bands-base bands-top number-layers index layer y0 y1 item occurrence
       main discontinuities distance x label-y line-bottom line-top layer-label-width accumulated row
       d0 d1 anchor-x margin box-left box-right box-bottom box-top box-height box-lines
       line-count total-distance discontinuity-index label-level)

  (setq x-origin (car insertion)
        y-origin (cadr insertion)
        number-layers (length layers)
        bands-base (+ y-origin (max (* 8.0 text-height) (* 0.20 interval)))
        bands-top (+ bands-base (* number-layers band-height))
        label-y (- y-origin (* 0.45 text-height))
        layer-label-width (max (* 1.20 interval) (* 24.0 text-height))
        total-distance (nth 5 (car (last rows))))

  (princ "\nDesenhando faixas do diagrama...")

  (setq index 0)
  (foreach layer layers
    (setq y0 (+ bands-base (* (- number-layers index 1) band-height))
          y1 (+ y0 band-height)
          item (assoc layer diagram-intervals))
    (if item
      (foreach occurrence (cdr item)
        (if (> (- (cadr occurrence) (car occurrence)) 1.0e-7)
          (qhpe:create-diagram-band
            space
            (+ x-origin (car occurrence))
            (+ x-origin (cadr occurrence))
            y0 y1 layer
          )
        )
      )
    )
    (qhpe:entmake-text-justified
      (list (- x-origin (* 0.35 text-height)) (+ y0 (/ band-height 2.0)) 0.0)
      layer
      text-height
      0.0
      2
      2
      annotation-layer
    )
    (setq index (1+ index))
  )

  (princ "\nInserindo linhas e textos de estaqueamento...")

  (setq main (qhpe:main-boundary-distances rows)
        line-bottom y-origin
        line-top bands-top)
  (foreach distance main
    (setq x (+ x-origin distance))
    (qhpe:entmake-line
      (list x line-bottom 0.0)
      (list x line-top 0.0)
      annotation-layer
    )
    ;; Centro horizontal exatamente sobre a linha da estaca.
    (qhpe:entmake-text-justified
      (list x label-y 0.0)
      (qhpe:format-station-diagram (+ start-station distance) interval)
      text-height
      0.0
      1
      3
      annotation-layer
    )
  )

  ;; Inicios e finais exatos das ocorrencias que nao coincidem com as
  ;; estacas principais. O rotulo fica rente a linha vertical.
  (setq discontinuities
    (qhpe:diagram-discontinuities-v17 diagram-intervals main total-distance)
        discontinuity-index 0)
  (foreach distance discontinuities
    (setq x (+ x-origin distance)
          label-level (rem discontinuity-index 2))
    (qhpe:entmake-line
      (list x y-origin 0.0)
      (list x bands-top 0.0)
      annotation-layer
    )
    (qhpe:entmake-text-justified
      (list
        (+ x (* 0.22 text-height))
        (+ y-origin (* (+ 0.65 (* label-level 0.55)) text-height))
        0.0
      )
      (qhpe:format-station-diagram (+ start-station distance) interval)
      text-height
      (/ pi 2.0)
      0
      0
      annotation-layer
    )
    (setq discontinuity-index (1+ discontinuity-index))
  )

  (princ "\nInserindo quadros de areas por trecho...")
  (setq accumulated nil)
  (foreach row rows
    ;; Atualiza o acumulado de cada servico incluindo o trecho atual.
    (foreach layer layers
      (setq accumulated
        (qhpe:update-accumulated
          accumulated
          layer
          (qhpe:row-area (nth 3 row) layer)
        )
      )
    )

    (setq d0 (nth 4 row)
          d1 (nth 5 row)
          anchor-x (+ x-origin d0)
          margin (* 0.38 text-height)
          box-left (+ anchor-x (* 0.18 text-height))
          box-right (- (+ x-origin d1) (* 0.18 text-height)))

    ;; Garante uma largura positiva ate no ultimo trecho parcial.
    (if (<= box-right (+ box-left (* 2.0 text-height)))
      (setq box-right (+ box-left (* 2.0 text-height)))
    )

    (setq box-lines (qhpe:row-box-lines row layers accumulated linear-factor start-station interval)
          line-count (length box-lines)
          box-height (+ (* line-count 1.45 text-height) (* 2.0 margin) (* 0.35 text-height))
          box-bottom (+ bands-top (* 1.30 text-height))
          box-top (+ box-bottom box-height))

    (qhpe:create-diagram-box-v17
      anchor-x bands-top box-left box-bottom box-right box-top
      margin text-height box-lines annotation-layer
    )
  )
  T
)





;;; ================================================================
;;;   - protege os dados de trechos contra sobrescrita dinamica;
;;;   - centraliza as estacas principais na respectiva linha;
;;;   - marca inicio e fim exatos de cada ocorrencia;
;;;   - cria um quadro em cada trecho com area e acumulado por layer;
;;;   - valida intervalos antes de acessar CAR/CADR.
;;; ================================================================

(setq qhpe:*version* "1.9")

;;; Redefinida para que o contador interno nao sobrescreva a variavel
;;; INTERVAL usada pelo comando principal (AutoLISP possui escopo dinamico).
(defun qhpe:merge-intervals (intervals tolerance / sorted current result interval-item)
  (setq sorted
    (vl-sort
      intervals
      '(lambda (left-item right-item)
         (< (car left-item) (car right-item))
       )
    )
  )
  (foreach interval-item sorted
    (if (null current)
      (setq current interval-item)
      (if (<= (car interval-item) (+ (cadr current) tolerance))
        (setq current
          (list
            (car current)
            (max (cadr current) (cadr interval-item))
          )
        )
        (progn
          (setq result (cons current result)
                current interval-item)
        )
      )
    )
  )
  (if current
    (setq result (cons current result))
  )
  (reverse result)
)

(defun qhpe:valid-diagram-row-p (candidate)
  (and
    (listp candidate)
    (>= (length candidate) 6)
    (numberp (nth 4 candidate))
    (numberp (nth 5 candidate))
  )
)

(defun qhpe:normalize-diagram-rows (data / result candidate)
  (cond
    ((qhpe:valid-diagram-row-p data)
     (list data)
    )
    ((listp data)
     (foreach candidate data
       (if (qhpe:valid-diagram-row-p candidate)
         (setq result (cons candidate result))
       )
     )
     (reverse result)
    )
  )
)

(defun qhpe:valid-number-interval-p (candidate)
  (and
    (listp candidate)
    (>= (length candidate) 2)
    (numberp (car candidate))
    (numberp (cadr candidate))
  )
)

(defun qhpe:valid-diagram-intervals (data / result layer-item occurrence valid-list)
  (foreach layer-item data
    (if (and (listp layer-item) (car layer-item))
      (progn
        (setq valid-list nil)
        (foreach occurrence (cdr layer-item)
          (if (qhpe:valid-number-interval-p occurrence)
            (setq valid-list
              (cons
                (list (car occurrence) (cadr occurrence))
                valid-list
              )
            )
          )
        )
        (setq result
          (cons
            (cons (car layer-item) (reverse valid-list))
            result
          )
        )
      )
    )
  )
  (reverse result)
)

(defun qhpe:last-row-distance-v18 (row-list / last-row)
  (if row-list
    (progn
      (setq last-row (car (last row-list)))
      (if (qhpe:valid-diagram-row-p last-row)
        (nth 5 last-row)
      )
    )
  )
)

(defun qhpe:main-boundary-distances-v18 (row-list / result current-row)
  (if row-list
    (progn
      (setq result (list (nth 4 (car row-list))))
      (foreach current-row row-list
        (setq result (append result (list (nth 5 current-row))))
      )
    )
  )
  result
)

(defun qhpe:diagram-discontinuities-v18
  (diagram-data main-distances total-distance / values layer-item occurrence start-value end-value candidate-value)
  (foreach layer-item diagram-data
    (foreach occurrence (cdr layer-item)
      (if (qhpe:valid-number-interval-p occurrence)
        (progn
          (setq start-value (car occurrence)
                end-value (cadr occurrence))
          (foreach candidate-value (list start-value end-value)
            (if
              (and
                (> candidate-value 0.01)
                (< candidate-value (- total-distance 0.01))
                (not (qhpe:near-any-p candidate-value main-distances 0.02))
              )
              (setq values (cons candidate-value values))
            )
          )
        )
      )
    )
  )
  (if values
    (qhpe:unique-numbers values 0.02)
    nil
  )
)

(defun qhpe:entmake-centered-station-text-v18
  (x-position y-position station-label text-height annotation-layer)
  (qhpe:entmake-text-justified
    (list x-position y-position 0.0)
    station-label
    text-height
    0.0
    1
    3
    annotation-layer
  )
)

;;; Gera todas as linhas do quadro. Todos os layers sao apresentados,
;;; inclusive quando a area do trecho e zero, para que cada estaca tenha
;;; um demonstrativo completo e os acumulados permanecam claros.
(defun qhpe:row-box-lines-v18
  (diagram-row layer-list accumulated linear-factor start-station station-step
    / result current-layer current-area accumulated-area)
  (setq result
    (list
      (strcat
        (qhpe:format-station-diagram
          (+ start-station (nth 4 diagram-row))
          station-step
        )
        " a "
        (qhpe:format-station-diagram
          (+ start-station (nth 5 diagram-row))
          station-step
        )
      )
    )
  )
  (foreach current-layer layer-list
    (setq current-area
      (max 0.0 (qhpe:row-area (nth 3 diagram-row) current-layer))
    )
    (setq accumulated-area
      (if (assoc current-layer accumulated)
        (max 0.0 (cdr (assoc current-layer accumulated)))
        0.0
      )
    )
    (setq result
      (append
        result
        (list
          (strcat
            current-layer
            " = "
            (qhpe:format-number
              (* current-area linear-factor linear-factor)
              2
            )
            " m2 | Acum. = "
            (qhpe:format-number
              (* accumulated-area linear-factor linear-factor)
              2
            )
            " m2"
          )
        )
      )
    )
  )
  result
)

(defun qhpe:create-linear-diagram-v18
  (space insertion source-rows source-layers source-diagram-data start-station station-step
    linear-factor band-height text-height annotation-layer
    / row-list layer-list diagram-data x-origin y-origin bands-base bands-top
      number-layers layer-index current-layer y0 y1 layer-item occurrence
      main-distances discontinuities current-distance x-position label-y
      line-bottom line-top accumulated current-row d0 d1 anchor-x margin
      box-left box-right box-bottom box-top box-height box-lines line-count
      total-distance discontinuity-index label-level current-area)

  (setq row-list (qhpe:normalize-diagram-rows source-rows)
        layer-list source-layers
        diagram-data (qhpe:valid-diagram-intervals source-diagram-data))

  (if (or (null row-list) (null layer-list))
    nil
    (progn
      (setq total-distance (qhpe:last-row-distance-v18 row-list))
      (if (not (numberp total-distance))
        nil
        (progn
          (setq x-origin (car insertion)
                y-origin (cadr insertion)
                number-layers (length layer-list)
                bands-base
                  (+
                    y-origin
                    (max (* 8.0 text-height) (* 0.20 station-step))
                  )
                bands-top (+ bands-base (* number-layers band-height))
                label-y (- y-origin (* 0.45 text-height)))

          (princ "\nDesenhando faixas do diagrama...")
          (setq layer-index 0)
          (foreach current-layer layer-list
            (setq y0
              (+
                bands-base
                (* (- number-layers layer-index 1) band-height)
              )
            )
            (setq y1 (+ y0 band-height)
                  layer-item (assoc current-layer diagram-data))

            (if layer-item
              (foreach occurrence (cdr layer-item)
                (if
                  (and
                    (qhpe:valid-number-interval-p occurrence)
                    (> (- (cadr occurrence) (car occurrence)) 1.0e-7)
                  )
                  (qhpe:create-diagram-band
                    space
                    (+ x-origin (car occurrence))
                    (+ x-origin (cadr occurrence))
                    y0
                    y1
                    current-layer
                  )
                )
              )
            )

            (qhpe:entmake-text-justified
              (list
                (- x-origin (* 0.35 text-height))
                (+ y0 (/ band-height 2.0))
                0.0
              )
              current-layer
              text-height
              0.0
              2
              2
              annotation-layer
            )
            (setq layer-index (1+ layer-index))
          )

          (princ "\nInserindo linhas e textos de estaqueamento...")
          (setq main-distances (qhpe:main-boundary-distances-v18 row-list)
                line-bottom y-origin
                line-top bands-top)

          (foreach current-distance main-distances
            (setq x-position (+ x-origin current-distance))
            (qhpe:entmake-line
              (list x-position line-bottom 0.0)
              (list x-position line-top 0.0)
              annotation-layer
            )
            (qhpe:entmake-centered-station-text-v18
              x-position
              label-y
              (qhpe:format-station-diagram
                (+ start-station current-distance)
                station-step
              )
              text-height
              annotation-layer
            )
          )

          (princ "\nInserindo estacas de inicio e termino das ocorrencias...")
          (setq discontinuities
            (qhpe:diagram-discontinuities-v18
              diagram-data
              main-distances
              total-distance
            )
          )
          (setq discontinuity-index 0)
          (foreach current-distance discontinuities
            (setq x-position (+ x-origin current-distance)
                  label-level (rem discontinuity-index 2))
            (qhpe:entmake-line
              (list x-position y-origin 0.0)
              (list x-position bands-top 0.0)
              annotation-layer
            )
            ;; Texto vertical, rente a linha exata do inicio/fim.
            (qhpe:entmake-text-justified
              (list
                (+ x-position (* 0.22 text-height))
                (+
                  y-origin
                  (* (+ 0.65 (* label-level 0.60)) text-height)
                )
                0.0
              )
              (qhpe:format-station-diagram
                (+ start-station current-distance)
                station-step
              )
              text-height
              (/ pi 2.0)
              0
              0
              annotation-layer
            )
            (setq discontinuity-index (1+ discontinuity-index))
          )

          (princ "\nInserindo quadros de areas por trecho...")
          (setq accumulated nil)
          (foreach current-row row-list
            ;; Atualiza o acumulado individual de cada layer antes de
            ;; escrever o quadro referente ao trecho atual.
            (foreach current-layer layer-list
              (setq current-area
                (max 0.0
                  (qhpe:row-area (nth 3 current-row) current-layer)
                )
              )
              (setq accumulated
                (qhpe:update-accumulated
                  accumulated
                  current-layer
                  current-area
                )
              )
            )

            (setq d0 (nth 4 current-row)
                  d1 (nth 5 current-row)
                  anchor-x (+ x-origin d0)
                  margin (* 0.34 text-height)
                  box-left (+ anchor-x (* 0.14 text-height))
                  box-right (- (+ x-origin d1) (* 0.14 text-height)))

            ;; O quadro permanece dentro do proprio trecho. Em trechos
            ;; muito curtos, conserva uma largura minima legivel.
            (if (<= box-right (+ box-left (* 2.0 text-height)))
              (setq box-right (+ box-left (* 2.0 text-height)))
            )

            (setq box-lines
              (qhpe:row-box-lines-v18
                current-row
                layer-list
                accumulated
                linear-factor
                start-station
                station-step
              )
            )
            (setq line-count (length box-lines)
                  box-height
                    (+
                      (* line-count 1.45 text-height)
                      (* 2.0 margin)
                      (* 0.35 text-height)
                    )
                  box-bottom (+ bands-top (* 1.25 text-height))
                  box-top (+ box-bottom box-height))

            (qhpe:create-diagram-box-v17
              anchor-x
              bands-top
              box-left
              box-bottom
              box-right
              box-top
              margin
              text-height
              box-lines
              annotation-layer
            )
          )
          T
        )
      )
    )
  )
)





;;; ================================================================
;;;
;;; QHPED  : desenha somente o diagrama linear, as faixas, as estacas
;;;          principais e os inicios/finais exatos das ocorrencias.
;;;
;;; QHPEA  : recalcula as areas e insere somente os quadros de area por
;;;          trecho e os acumulados por layer. O usuario deve clicar no
;;;          mesmo ponto inicial usado no QHPED.
;;;
;;; A separacao impede que os dados temporarios de regioes/Boolean sejam
;;; transportados para a etapa de desenho dos quadros.
;;; ================================================================

(setq qhpe:*version* "2.0")

(defun qhpe2:station-distances
  (q2_total q2_start-station q2_station-step
    / q2_result q2_remainder q2_first q2_distance q2_tolerance)
  (setq q2_tolerance (* q2_station-step 1.0e-8)
        q2_remainder
          (-
            q2_start-station
            (* (fix (/ q2_start-station q2_station-step)) q2_station-step)
          )
        q2_result (list 0.0))
  (if (< (abs q2_remainder) q2_tolerance)
    (setq q2_first q2_station-step)
    (setq q2_first (- q2_station-step q2_remainder))
  )
  (setq q2_distance q2_first)
  (while (< q2_distance (- q2_total q2_tolerance))
    (setq q2_result (append q2_result (list q2_distance))
          q2_distance (+ q2_distance q2_station-step))
  )
  (if
    (or
      (null q2_result)
      (> (abs (- (car (last q2_result)) q2_total)) q2_tolerance)
    )
    (setq q2_result (append q2_result (list q2_total)))
  )
  q2_result
)

(defun qhpe2:layer-total-from-records
  (q2_records q2_layer / q2_total q2_record q2_area)
  (setq q2_total 0.0)
  (foreach q2_record q2_records
    (if (= (nth 0 q2_record) q2_layer)
      (progn
        (setq q2_area (qhpe:safe-area (nth 2 q2_record)))
        (if (numberp q2_area)
          (setq q2_total
            (+ q2_total (* (nth 1 q2_record) q2_area))
          )
        )
      )
    )
  )
  (max 0.0 q2_total)
)

(defun qhpe2:sort-layers-from-records
  (q2_layers q2_records / q2_pairs)
  (setq q2_pairs
    (mapcar
      '(lambda (q2_layer-name)
         (cons
           q2_layer-name
           (qhpe2:layer-total-from-records q2_records q2_layer-name)
         )
       )
      q2_layers
    )
  )
  (setq q2_pairs
    (vl-sort
      q2_pairs
      '(lambda (q2_left q2_right)
         (if (equal (cdr q2_left) (cdr q2_right) 1.0e-9)
           (< (strcmp (car q2_left) (car q2_right)) 0)
           (> (cdr q2_left) (cdr q2_right))
         )
       )
    )
  )
  (mapcar 'car q2_pairs)
)

(defun qhpe2:merged-projections
  (q2_layers q2_projected / q2_result q2_layer-name q2_item q2_intervals)
  (foreach q2_layer-name q2_layers
    (setq q2_item (assoc q2_layer-name q2_projected)
          q2_intervals (if q2_item (cdr q2_item) nil))
    (if q2_intervals
      (setq q2_intervals (qhpe:merge-intervals q2_intervals 0.01))
    )
    (setq q2_result
      (cons (cons q2_layer-name q2_intervals) q2_result)
    )
  )
  (reverse q2_result)
)

(defun qhpe2:draw-base-diagram
  (q2_insertion q2_total q2_layers q2_diagram-data q2_start-station
    q2_station-step q2_band-height q2_text-height q2_annotation-layer
    / q2_x-origin q2_y-origin q2_band-base q2_band-top q2_layer-count
      q2_layer-index q2_layer-name q2_y0 q2_y1 q2_layer-item
      q2_occurrence q2_main-distances q2_distance q2_x q2_label-y
      q2_discontinuities q2_disc-index q2_level)

  (setq q2_x-origin (car q2_insertion)
        q2_y-origin (cadr q2_insertion)
        q2_layer-count (length q2_layers)
        q2_band-base
          (+
            q2_y-origin
            (max (* 2.20 q2_text-height) (* 0.08 q2_station-step))
          )
        q2_band-top (+ q2_band-base (* q2_layer-count q2_band-height))
        q2_label-y (- q2_y-origin (* 0.45 q2_text-height)))

  (princ "\nDesenhando as faixas dos servicos...")
  (setq q2_layer-index 0)
  (foreach q2_layer-name q2_layers
    (setq q2_y0
      (+
        q2_band-base
        (* (- q2_layer-count q2_layer-index 1) q2_band-height)
      )
    )
    (setq q2_y1 (+ q2_y0 q2_band-height)
          q2_layer-item (assoc q2_layer-name q2_diagram-data))

    (if q2_layer-item
      (foreach q2_occurrence (cdr q2_layer-item)
        (if
          (and
            (qhpe:valid-number-interval-p q2_occurrence)
            (> (- (cadr q2_occurrence) (car q2_occurrence)) 1.0e-7)
          )
          (qhpe:create-diagram-band
            nil
            (+ q2_x-origin (car q2_occurrence))
            (+ q2_x-origin (cadr q2_occurrence))
            q2_y0
            q2_y1
            q2_layer-name
          )
        )
      )
    )

    (qhpe:entmake-text-justified
      (list
        (- q2_x-origin (* 0.35 q2_text-height))
        (+ q2_y0 (/ q2_band-height 2.0))
        0.0
      )
      q2_layer-name
      q2_text-height
      0.0
      2
      2
      q2_annotation-layer
    )
    (setq q2_layer-index (1+ q2_layer-index))
  )

  (princ "\nInserindo linhas e textos das estacas principais...")
  (setq q2_main-distances
    (qhpe2:station-distances q2_total q2_start-station q2_station-step)
  )
  (foreach q2_distance q2_main-distances
    (setq q2_x (+ q2_x-origin q2_distance))
    (qhpe:entmake-line
      (list q2_x q2_y-origin 0.0)
      (list q2_x q2_band-top 0.0)
      q2_annotation-layer
    )
    ;; Centro/Bottom: o ponto do texto coincide exatamente com a linha.
    (qhpe:entmake-text-justified
      (list q2_x q2_label-y 0.0)
      (qhpe:format-station-diagram
        (+ q2_start-station q2_distance)
        q2_station-step
      )
      q2_text-height
      0.0
      1
      3
      q2_annotation-layer
    )
  )

  (princ "\nInserindo estacas exatas de inicio e termino dos trechos...")
  (setq q2_discontinuities
    (qhpe:discontinuity-distances
      q2_diagram-data
      q2_main-distances
      q2_total
    )
  )
  (setq q2_disc-index 0)
  (foreach q2_distance q2_discontinuities
    (setq q2_x (+ q2_x-origin q2_distance)
          q2_level (rem q2_disc-index 2))
    (qhpe:entmake-line
      (list q2_x q2_y-origin 0.0)
      (list q2_x q2_band-top 0.0)
      q2_annotation-layer
    )
    (qhpe:entmake-text-justified
      (list
        (+ q2_x (* 0.22 q2_text-height))
        (+
          q2_y-origin
          (* (+ 0.65 (* q2_level 0.65)) q2_text-height)
        )
        0.0
      )
      (qhpe:format-station-diagram
        (+ q2_start-station q2_distance)
        q2_station-step
      )
      q2_text-height
      (/ pi 2.0)
      0
      0
      q2_annotation-layer
    )
    (setq q2_disc-index (1+ q2_disc-index))
  )
  T
)

(defun qhpe2:box-lines
  (q2_row q2_layers q2_accumulated q2_linear-factor q2_start-station
    q2_station-step
    / q2_result q2_layer-name q2_area q2_acc-area)
  (setq q2_result
    (list
      (strcat
        (qhpe:format-station-diagram
          (+ q2_start-station (nth 4 q2_row))
          q2_station-step
        )
        " a "
        (qhpe:format-station-diagram
          (+ q2_start-station (nth 5 q2_row))
          q2_station-step
        )
      )
    )
  )
  (foreach q2_layer-name q2_layers
    (setq q2_area
      (max 0.0 (qhpe:row-area (nth 3 q2_row) q2_layer-name))
    )
    (setq q2_acc-area
      (if (assoc q2_layer-name q2_accumulated)
        (max 0.0 (cdr (assoc q2_layer-name q2_accumulated)))
        0.0
      )
    )
    (setq q2_result
      (append
        q2_result
        (list
          (strcat
            q2_layer-name
            " = "
            (qhpe:format-number
              (* q2_area q2_linear-factor q2_linear-factor)
              2
            )
            " m2 | Acum. = "
            (qhpe:format-number
              (* q2_acc-area q2_linear-factor q2_linear-factor)
              2
            )
            " m2"
          )
        )
      )
    )
  )
  q2_result
)

(defun qhpe2:draw-area-boxes
  (q2_insertion q2_rows q2_layers q2_start-station q2_station-step
    q2_linear-factor q2_band-height q2_text-height q2_annotation-layer
    / q2_x-origin q2_y-origin q2_layer-count q2_band-base q2_band-top
      q2_accumulated q2_row q2_layer-name q2_current-area q2_d0 q2_d1
      q2_anchor-x q2_box-left q2_box-right q2_margin q2_box-lines
      q2_line-count q2_box-height q2_box-bottom q2_box-top)

  (setq q2_x-origin (car q2_insertion)
        q2_y-origin (cadr q2_insertion)
        q2_layer-count (length q2_layers)
        q2_band-base
          (+
            q2_y-origin
            (max (* 2.20 q2_text-height) (* 0.08 q2_station-step))
          )
        q2_band-top (+ q2_band-base (* q2_layer-count q2_band-height))
        q2_accumulated nil)

  (princ "\nInserindo os quadros de area por estaca...")
  (foreach q2_row q2_rows
    (foreach q2_layer-name q2_layers
      (setq q2_current-area
        (max 0.0 (qhpe:row-area (nth 3 q2_row) q2_layer-name))
      )
      (setq q2_accumulated
        (qhpe:update-accumulated
          q2_accumulated
          q2_layer-name
          q2_current-area
        )
      )
    )

    (setq q2_d0 (nth 4 q2_row)
          q2_d1 (nth 5 q2_row)
          q2_anchor-x (+ q2_x-origin q2_d0)
          q2_margin (* 0.34 q2_text-height)
          ;; O quadro fica rente a linha da estaca inicial.
          q2_box-left (+ q2_anchor-x (* 0.14 q2_text-height))
          q2_box-right
            (- (+ q2_x-origin q2_d1) (* 0.14 q2_text-height)))

    (if (<= q2_box-right (+ q2_box-left (* 2.0 q2_text-height)))
      (setq q2_box-right (+ q2_box-left (* 2.0 q2_text-height)))
    )

    (setq q2_box-lines
      (qhpe2:box-lines
        q2_row
        q2_layers
        q2_accumulated
        q2_linear-factor
        q2_start-station
        q2_station-step
      )
    )
    (setq q2_line-count (length q2_box-lines)
          q2_box-height
            (+
              (* q2_line-count 1.45 q2_text-height)
              (* 2.0 q2_margin)
              (* 0.35 q2_text-height)
            )
          q2_box-bottom (+ q2_band-top (* 1.25 q2_text-height))
          q2_box-top (+ q2_box-bottom q2_box-height))

    (qhpe:create-diagram-box-v17
      q2_anchor-x
      q2_band-top
      q2_box-left
      q2_box-bottom
      q2_box-right
      q2_box-top
      q2_margin
      q2_text-height
      q2_box-lines
      q2_annotation-layer
    )
  )
  T
)

(defun qhpe2:restore-environment
  (q2_doc q2_old-clayer q2_old-cmdecho q2_old-osmode q2_old-error)
  (qhpe:safe-set-clayer q2_old-clayer)
  (vl-catch-all-apply 'setvar (list "CMDECHO" q2_old-cmdecho))
  (vl-catch-all-apply 'setvar (list "OSMODE" q2_old-osmode))
  (vl-catch-all-apply 'vla-EndUndoMark (list q2_doc))
  (setq *error* q2_old-error)
  T
)





;;; ================================================================
;;;
;;; Esta secao redefine apenas a etapa grafica dos comandos QHPED e
;;;
;;; Melhorias:
;;;   - estaqueamento principal criado por ENTMAKEX, sem ActiveX;
;;;   - textos das estacas centralizados exatamente nas linhas;
;;;   - inicio e fim de cada descontinuidade com linha e estaca exata;
;;;   - quadros centralizados entre as estacas;
;;;   - textos dos quadros no estilo QHPE_ROMANS (romans.shx);
;;;   - retangulo dimensionado pelo tamanho real dos textos;
;;;   - unidade exibida como m\U+00B2.
;;; ================================================================

(setq qhpe:*version* "2.1")
(setq qhpe21:*text-style* "QHPE_ROMANS")

(defun qhpe21:ensure-romans-style
  (/ q21_acad q21_doc q21_styles q21_style q21_result)
  (setq q21_acad (vlax-get-acad-object)
        q21_doc (vla-get-ActiveDocument q21_acad)
        q21_styles (vla-get-TextStyles q21_doc))

  (if (tblsearch "STYLE" qhpe21:*text-style*)
    (setq q21_result
      (vl-catch-all-apply
        'vla-Item
        (list q21_styles qhpe21:*text-style*)
      ))
    (setq q21_result
      (vl-catch-all-apply
        'vla-Add
        (list q21_styles qhpe21:*text-style*)
      ))
  )

  (if (not (vl-catch-all-error-p q21_result))
    (progn
      (setq q21_style q21_result)
      ;; Reforca a fonte mesmo quando o estilo ja existia no desenho.
      (vl-catch-all-apply
        'vla-put-FontFile
        (list q21_style "romans.shx")
      )
      (vl-catch-all-apply 'vla-put-Height (list q21_style 0.0))
    )
  )

  (if (tblsearch "STYLE" qhpe21:*text-style*)
    qhpe21:*text-style*
    (getvar "TEXTSTYLE")
  )
)

(defun qhpe21:make-line (q21_p1 q21_p2 q21_layer)
  (entmakex
    (list
      '(0 . "LINE")
      '(100 . "AcDbEntity")
      (cons 8 q21_layer)
      '(100 . "AcDbLine")
      (cons 10 (list (car q21_p1) (cadr q21_p1) 0.0))
      (cons 11 (list (car q21_p2) (cadr q21_p2) 0.0))
      '(210 0.0 0.0 1.0)
    )
  )
)

(defun qhpe21:make-rectangle
  (q21_x0 q21_y0 q21_x1 q21_y1 q21_layer)
  (entmakex
    (list
      '(0 . "LWPOLYLINE")
      '(100 . "AcDbEntity")
      (cons 8 q21_layer)
      '(100 . "AcDbPolyline")
      '(90 . 4)
      '(70 . 1)
      (cons 10 (list q21_x0 q21_y0))
      (cons 10 (list q21_x1 q21_y0))
      (cons 10 (list q21_x1 q21_y1))
      (cons 10 (list q21_x0 q21_y1))
      '(210 0.0 0.0 1.0)
    )
  )
)

(defun qhpe21:make-solid
  (q21_x0 q21_y0 q21_x1 q21_y1 q21_layer)
  (entmakex
    (list
      '(0 . "SOLID")
      '(100 . "AcDbEntity")
      (cons 8 q21_layer)
      '(100 . "AcDbTrace")
      (cons 10 (list q21_x0 q21_y0 0.0))
      (cons 11 (list q21_x1 q21_y0 0.0))
      (cons 12 (list q21_x0 q21_y1 0.0))
      (cons 13 (list q21_x1 q21_y1 0.0))
      '(210 0.0 0.0 1.0)
    )
  )
)

(defun qhpe21:make-band
  (q21_x0 q21_x1 q21_y0 q21_y1 q21_layer)
  (if (> (- q21_x1 q21_x0) 1.0e-8)
    (progn
      (qhpe21:make-solid q21_x0 q21_y0 q21_x1 q21_y1 q21_layer)
      (qhpe21:make-rectangle q21_x0 q21_y0 q21_x1 q21_y1 q21_layer)
      T
    )
  )
)

(defun qhpe21:make-text-left
  (q21_point q21_text q21_height q21_rotation q21_layer q21_style)
  (entmakex
    (list
      '(0 . "TEXT")
      '(100 . "AcDbEntity")
      (cons 8 q21_layer)
      '(100 . "AcDbText")
      (cons 10 (list (car q21_point) (cadr q21_point) 0.0))
      (cons 40 q21_height)
      (cons 1 q21_text)
      (cons 50 q21_rotation)
      (cons 7 q21_style)
      '(72 . 0)
      '(73 . 0)
      '(210 0.0 0.0 1.0)
    )
  )
)

(defun qhpe21:make-text-aligned
  (q21_point q21_text q21_height q21_rotation q21_hjust q21_vjust
    q21_layer q21_style)
  (entmakex
    (list
      '(0 . "TEXT")
      '(100 . "AcDbEntity")
      (cons 8 q21_layer)
      '(100 . "AcDbText")
      (cons 10 (list (car q21_point) (cadr q21_point) 0.0))
      (cons 11 (list (car q21_point) (cadr q21_point) 0.0))
      (cons 40 q21_height)
      (cons 1 q21_text)
      (cons 50 q21_rotation)
      (cons 7 q21_style)
      (cons 72 q21_hjust)
      (cons 73 q21_vjust)
      '(210 0.0 0.0 1.0)
    )
  )
)

(defun qhpe21:near-number-p
  (q21_value q21_values q21_tolerance / q21_found q21_item)
  (setq q21_found nil)
  (foreach q21_item q21_values
    (if
      (and
        (numberp q21_item)
        (<= (abs (- q21_value q21_item)) q21_tolerance)
      )
      (setq q21_found T)
    )
  )
  q21_found
)

(defun qhpe21:add-unique-number
  (q21_value q21_values q21_tolerance)
  (if (qhpe21:near-number-p q21_value q21_values q21_tolerance)
    q21_values
    (cons q21_value q21_values)
  )
)

(defun qhpe21:main-station-distances
  (q21_total q21_start q21_step
    / q21_result q21_remainder q21_first q21_distance q21_tolerance)
  (setq q21_tolerance (max 1.0e-8 (* q21_step 1.0e-8))
        q21_result (list 0.0)
        q21_remainder
          (- q21_start (* (fix (/ q21_start q21_step)) q21_step)))

  (if (< q21_remainder 0.0)
    (setq q21_remainder (+ q21_remainder q21_step))
  )

  (if (< (abs q21_remainder) q21_tolerance)
    (setq q21_first q21_step)
    (setq q21_first (- q21_step q21_remainder))
  )

  (setq q21_distance q21_first)
  (while (< q21_distance (- q21_total q21_tolerance))
    (setq q21_result (cons q21_distance q21_result)
          q21_distance (+ q21_distance q21_step))
  )

  (if
    (not
      (qhpe21:near-number-p q21_total q21_result q21_tolerance)
    )
    (setq q21_result (cons q21_total q21_result))
  )
  (vl-sort q21_result '<)
)

(defun qhpe21:discontinuity-distances
  (q21_diagram-data q21_main q21_total
    / q21_result q21_layer-item q21_occurrence q21_value)
  (setq q21_result nil)
  (foreach q21_layer-item q21_diagram-data
    (if (listp q21_layer-item)
      (foreach q21_occurrence (cdr q21_layer-item)
        (if
          (and
            (listp q21_occurrence)
            (numberp (car q21_occurrence))
            (numberp (cadr q21_occurrence))
          )
          (foreach q21_value
            (list (car q21_occurrence) (cadr q21_occurrence))
            (if
              (and
                (> q21_value 0.01)
                (< q21_value (- q21_total 0.01))
                (not (qhpe21:near-number-p q21_value q21_main 0.02))
              )
              (setq q21_result
                (qhpe21:add-unique-number q21_value q21_result 0.02)
              )
            )
          )
        )
      )
    )
  )
  (vl-sort q21_result '<)
)

(defun qhpe21:draw-base-diagram
  (q21_insertion q21_total q21_layers q21_diagram-data q21_start-station
    q21_station-step q21_band-height q21_text-height q21_annotation-layer
    / q21_x0 q21_y0 q21_band-base q21_band-top q21_layer-count
      q21_layer-index q21_layer-name q21_layer-data q21_occurrence
      q21_by0 q21_by1 q21_style q21_main q21_distance q21_x
      q21_label-y q21_discontinuities q21_disc-index q21_disc-level
      q21_station-label)

  (setq q21_x0 (car q21_insertion)
        q21_y0 (cadr q21_insertion)
        q21_layer-count (length q21_layers)
        q21_style (qhpe21:ensure-romans-style)
        q21_band-base
          (+
            q21_y0
            (max (* 2.35 q21_text-height) (* 0.08 q21_station-step))
          )
        q21_band-top
          (+ q21_band-base (* q21_layer-count q21_band-height))
        q21_label-y (- q21_y0 (* 0.65 q21_text-height)))

  (princ "\nDesenhando as faixas dos servicos...")
  (setq q21_layer-index 0)
  (foreach q21_layer-name q21_layers
    (setq q21_by0
      (+
        q21_band-base
        (* (- q21_layer-count q21_layer-index 1) q21_band-height)
      )
    )
    (setq q21_by1 (+ q21_by0 q21_band-height)
          q21_layer-data (assoc q21_layer-name q21_diagram-data))

    (if q21_layer-data
      (foreach q21_occurrence (cdr q21_layer-data)
        (if
          (and
            (listp q21_occurrence)
            (numberp (car q21_occurrence))
            (numberp (cadr q21_occurrence))
            (> (- (cadr q21_occurrence) (car q21_occurrence)) 1.0e-8)
          )
          (qhpe21:make-band
            (+ q21_x0 (car q21_occurrence))
            (+ q21_x0 (cadr q21_occurrence))
            q21_by0
            q21_by1
            q21_layer-name
          )
        )
      )
    )

    ;; Nome do servico alinhado a direita e centralizado na faixa.
    (qhpe21:make-text-aligned
      (list
        (- q21_x0 (* 0.40 q21_text-height))
        (+ q21_by0 (/ q21_band-height 2.0))
        0.0
      )
      q21_layer-name
      q21_text-height
      0.0
      2
      2
      q21_annotation-layer
      q21_style
    )
    (setq q21_layer-index (1+ q21_layer-index))
  )

  (princ "\nInserindo linhas e textos das estacas principais...")
  (setq q21_main
    (qhpe21:main-station-distances
      q21_total q21_start-station q21_station-step
    )
  )
  (foreach q21_distance q21_main
    (if (numberp q21_distance)
      (progn
        (setq q21_x (+ q21_x0 q21_distance)
              q21_station-label
                (qhpe:format-station-diagram
                  (+ q21_start-station q21_distance)
                  q21_station-step
                ))
        (qhpe21:make-line
          (list q21_x q21_y0 0.0)
          (list q21_x q21_band-top 0.0)
          q21_annotation-layer
        )
        ;; Centro horizontal exatamente coincidente com a linha vertical.
        (qhpe21:make-text-aligned
          (list q21_x q21_label-y 0.0)
          q21_station-label
          q21_text-height
          0.0
          1
          0
          q21_annotation-layer
          q21_style
        )
      )
    )
  )

  (princ "\nInserindo estacas de inicio e fim das descontinuidades...")
  (setq q21_discontinuities
    (qhpe21:discontinuity-distances
      q21_diagram-data q21_main q21_total
    )
  )
  (setq q21_disc-index 0)
  (foreach q21_distance q21_discontinuities
    (if (numberp q21_distance)
      (progn
        (setq q21_x (+ q21_x0 q21_distance)
              q21_disc-level (rem q21_disc-index 2)
              q21_station-label
                (qhpe:format-station-diagram
                  (+ q21_start-station q21_distance)
                  q21_station-step
                ))
        (qhpe21:make-line
          (list q21_x q21_y0 0.0)
          (list q21_x q21_band-top 0.0)
          q21_annotation-layer
        )
        ;; O texto vertical fica rente a linha da descontinuidade.
        (qhpe21:make-text-left
          (list
            (- q21_x (* 0.24 q21_text-height))
            (+
              q21_y0
              (* (+ 0.45 (* q21_disc-level 0.65)) q21_text-height)
            )
            0.0
          )
          q21_station-label
          q21_text-height
          (/ pi 2.0)
          q21_annotation-layer
          q21_style
        )
        (setq q21_disc-index (1+ q21_disc-index))
      )
    )
  )
  T
)

(defun qhpe2:draw-base-diagram
  (q21_insertion q21_total q21_layers q21_diagram-data q21_start-station
    q21_station-step q21_band-height q21_text-height q21_annotation-layer)
  (qhpe21:draw-base-diagram
    q21_insertion q21_total q21_layers q21_diagram-data q21_start-station
    q21_station-step q21_band-height q21_text-height q21_annotation-layer
  )
)

(defun qhpe21:box-lines
  (q21_row q21_layers q21_accumulated q21_linear-factor
    q21_start-station q21_station-step
    / q21_result q21_layer q21_area q21_acc-area)
  (setq q21_result
    (list
      (strcat
        (qhpe:format-station-diagram
          (+ q21_start-station (nth 4 q21_row))
          q21_station-step
        )
        " a "
        (qhpe:format-station-diagram
          (+ q21_start-station (nth 5 q21_row))
          q21_station-step
        )
      )
    )
  )
  (foreach q21_layer q21_layers
    (setq q21_area
      (max 0.0 (qhpe:row-area (nth 3 q21_row) q21_layer))
    )
    (setq q21_acc-area
      (if (assoc q21_layer q21_accumulated)
        (max 0.0 (cdr (assoc q21_layer q21_accumulated)))
        0.0
      )
    )
    (setq q21_result
      (append
        q21_result
        (list
          (strcat
            q21_layer
            " = "
            (qhpe:format-number
              (* q21_area q21_linear-factor q21_linear-factor)
              2
            )
            " m\\U+00B2 | Acum. = "
            (qhpe:format-number
              (* q21_acc-area q21_linear-factor q21_linear-factor)
              2
            )
            " m\\U+00B2"
          )
        )
      )
    )
  )
  q21_result
)

(defun qhpe21:text-extents
  (q21_text q21_height q21_style
    / q21_box q21_min q21_max q21_minx q21_miny q21_maxx q21_maxy)
  (setq q21_box
    (textbox
      (list
        '(0 . "TEXT")
        (cons 1 q21_text)
        (cons 40 q21_height)
        (cons 7 q21_style)
        '(50 . 0.0)
      )
    )
  )
  (if
    (and
      (listp q21_box)
      (listp (car q21_box))
      (listp (cadr q21_box))
    )
    (progn
      (setq q21_min (car q21_box)
            q21_max (cadr q21_box)
            q21_minx (car q21_min)
            q21_miny (cadr q21_min)
            q21_maxx (car q21_max)
            q21_maxy (cadr q21_max))
      (list q21_minx q21_miny q21_maxx q21_maxy
        (- q21_maxx q21_minx)
        (- q21_maxy q21_miny))
    )
    (list 0.0 0.0 (* (strlen q21_text) q21_height 0.62)
      q21_height (* (strlen q21_text) q21_height 0.62) q21_height)
  )
)

(defun qhpe21:line-metrics
  (q21_lines q21_height q21_style / q21_result q21_line q21_ext)
  (foreach q21_line q21_lines
    (setq q21_ext (qhpe21:text-extents q21_line q21_height q21_style)
          q21_result (cons (cons q21_line q21_ext) q21_result))
  )
  (reverse q21_result)
)

(defun qhpe21:max-metric-width
  (q21_metrics / q21_max q21_metric)
  (setq q21_max 0.0)
  (foreach q21_metric q21_metrics
    (if (> (nth 5 q21_metric) q21_max)
      (setq q21_max (nth 5 q21_metric))
    )
  )
  q21_max
)

(defun qhpe21:metrics-height
  (q21_metrics q21_gap / q21_total q21_count q21_metric)
  (setq q21_total 0.0
        q21_count 0)
  (foreach q21_metric q21_metrics
    (setq q21_total (+ q21_total (nth 6 q21_metric))
          q21_count (1+ q21_count))
  )
  (if (> q21_count 1)
    (setq q21_total (+ q21_total (* (1- q21_count) q21_gap)))
  )
  q21_total
)

(defun qhpe21:draw-one-area-box
  (q21_mid-x q21_available-width q21_bottom q21_lines
    q21_preferred-height q21_layer q21_style
    / q21_metrics q21_max-width q21_scale q21_height q21_gap q21_margin
      q21_content-height q21_box-width q21_box-height q21_left q21_right
      q21_top q21_cursor-top q21_metric q21_text q21_minx q21_maxy
      q21_width q21_line-height q21_insert-x q21_insert-y)

  ;; Mede primeiro na altura solicitada e reduz somente se necessario para
  ;; manter o quadro dentro do intervalo entre as duas estacas.
  (setq q21_height q21_preferred-height
        q21_metrics (qhpe21:line-metrics q21_lines q21_height q21_style)
        q21_max-width (qhpe21:max-metric-width q21_metrics))

  (if
    (and
      (> q21_max-width 1.0e-9)
      (> q21_max-width (* 0.88 q21_available-width))
    )
    (progn
      (setq q21_scale
        (/ (* 0.88 q21_available-width) q21_max-width))
      (setq q21_height
        (* q21_preferred-height (max 0.20 (min 1.0 q21_scale))))
      (setq q21_metrics
        (qhpe21:line-metrics q21_lines q21_height q21_style)
      )
      (setq q21_max-width (qhpe21:max-metric-width q21_metrics))
    )
  )

  (setq q21_gap (* 0.30 q21_height)
        q21_margin (* 0.32 q21_height)
        q21_content-height (qhpe21:metrics-height q21_metrics q21_gap)
        q21_box-width (+ q21_max-width (* 2.0 q21_margin))
        q21_box-height (+ q21_content-height (* 2.0 q21_margin))
        q21_left (- q21_mid-x (/ q21_box-width 2.0))
        q21_right (+ q21_mid-x (/ q21_box-width 2.0))
        q21_top (+ q21_bottom q21_box-height)
        q21_cursor-top (- q21_top q21_margin))

  (qhpe21:make-rectangle
    q21_left q21_bottom q21_right q21_top q21_layer
  )

  (foreach q21_metric q21_metrics
    ;; Estrutura: (texto minX minY maxX maxY largura altura)
    (setq q21_text (car q21_metric)
          q21_minx (nth 1 q21_metric)
          q21_maxy (nth 4 q21_metric)
          q21_width (nth 5 q21_metric)
          q21_line-height (nth 6 q21_metric)
          q21_insert-x (- (- q21_mid-x (/ q21_width 2.0)) q21_minx)
          q21_insert-y (- q21_cursor-top q21_maxy))

    (qhpe21:make-text-left
      (list q21_insert-x q21_insert-y 0.0)
      q21_text
      q21_height
      0.0
      q21_layer
      q21_style
    )
    (setq q21_cursor-top
      (- q21_cursor-top q21_line-height q21_gap))
  )
  T
)

(defun qhpe21:draw-area-boxes
  (q21_insertion q21_rows q21_layers q21_start-station q21_station-step
    q21_linear-factor q21_band-height q21_text-height q21_annotation-layer
    / q21_x0 q21_y0 q21_layer-count q21_band-base q21_band-top
      q21_accumulated q21_row q21_layer q21_current-area q21_d0 q21_d1
      q21_mid-x q21_available-width q21_box-bottom q21_lines q21_style)

  (setq q21_x0 (car q21_insertion)
        q21_y0 (cadr q21_insertion)
        q21_layer-count (length q21_layers)
        q21_style (qhpe21:ensure-romans-style)
        q21_band-base
          (+
            q21_y0
            (max (* 2.35 q21_text-height) (* 0.08 q21_station-step))
          )
        q21_band-top
          (+ q21_band-base (* q21_layer-count q21_band-height))
        q21_accumulated nil)

  (princ "\nInserindo quadros centralizados entre as estacas...")
  (foreach q21_row q21_rows
    ;; Atualiza o acumulado de cada servico antes de montar as linhas.
    (foreach q21_layer q21_layers
      (setq q21_current-area
        (max 0.0 (qhpe:row-area (nth 3 q21_row) q21_layer))
      )
      (setq q21_accumulated
        (qhpe:update-accumulated
          q21_accumulated q21_layer q21_current-area
        )
      )
    )

    (setq q21_d0 (nth 4 q21_row)
          q21_d1 (nth 5 q21_row))
    (if
      (and
        (numberp q21_d0)
        (numberp q21_d1)
        (> q21_d1 q21_d0)
      )
      (progn
        (setq q21_mid-x
          (+ q21_x0 (/ (+ q21_d0 q21_d1) 2.0)))
        (setq q21_available-width
          (max (* 2.0 q21_text-height)
            (- q21_d1 q21_d0 (* 0.35 q21_text-height))))
        (setq q21_box-bottom
          (+ q21_band-top (* 0.70 q21_text-height)))
        (setq q21_lines
          (qhpe21:box-lines
            q21_row q21_layers q21_accumulated q21_linear-factor
            q21_start-station q21_station-step
          )
        )
        (qhpe21:draw-one-area-box
          q21_mid-x
          q21_available-width
          q21_box-bottom
          q21_lines
          q21_text-height
          q21_annotation-layer
          q21_style
        )
      )
    )
  )
  T
)

(defun qhpe2:draw-area-boxes
  (q21_insertion q21_rows q21_layers q21_start-station q21_station-step
    q21_linear-factor q21_band-height q21_text-height q21_annotation-layer)
  (qhpe21:draw-area-boxes
    q21_insertion q21_rows q21_layers q21_start-station q21_station-step
    q21_linear-factor q21_band-height q21_text-height q21_annotation-layer
  )
)

;;; ================================================================
;;;
;;;   - linhas de inicio/fim de ocorrencia estendidas abaixo do diagrama;
;;;   - textos verticais das descontinuidades deslocados para baixo;
;;;   - quadros substituidos por tabelas graficas;
;;;   - titulo com o intervalo de estacas;
;;;   - colunas SERVICO, AREA e ACUMULADO;
;;;   - unidade m\U+00B2 e estilo QHPE_ROMANS.
;;; ================================================================

(setq qhpe:*version* "2.2")

(defun qhpe22:draw-base-diagram
  (q22_insertion q22_total q22_layers q22_diagram-data q22_start-station
    q22_station-step q22_band-height q22_text-height q22_annotation-layer
    / q22_x0 q22_y0 q22_band-base q22_band-top q22_layer-count
      q22_layer-index q22_layer-name q22_layer-data q22_occurrence
      q22_by0 q22_by1 q22_style q22_main q22_distance q22_x
      q22_label-y q22_discontinuities q22_disc-index q22_disc-level
      q22_station-label q22_disc-line-bottom q22_disc-text-y)

  (setq q22_x0 (car q22_insertion)
        q22_y0 (cadr q22_insertion)
        q22_layer-count (length q22_layers)
        q22_style (qhpe21:ensure-romans-style)
        q22_band-base
          (+
            q22_y0
            (max (* 2.35 q22_text-height) (* 0.08 q22_station-step))
          )
        q22_band-top
          (+ q22_band-base (* q22_layer-count q22_band-height))
        q22_label-y (- q22_y0 (* 0.65 q22_text-height)))

  (princ "\nDesenhando as faixas dos servicos...")
  (setq q22_layer-index 0)
  (foreach q22_layer-name q22_layers
    (setq q22_by0
      (+
        q22_band-base
        (* (- q22_layer-count q22_layer-index 1) q22_band-height)
      )
    )
    (setq q22_by1 (+ q22_by0 q22_band-height)
          q22_layer-data (assoc q22_layer-name q22_diagram-data))

    (if q22_layer-data
      (foreach q22_occurrence (cdr q22_layer-data)
        (if
          (and
            (listp q22_occurrence)
            (numberp (car q22_occurrence))
            (numberp (cadr q22_occurrence))
            (> (- (cadr q22_occurrence) (car q22_occurrence)) 1.0e-8)
          )
          (qhpe21:make-band
            (+ q22_x0 (car q22_occurrence))
            (+ q22_x0 (cadr q22_occurrence))
            q22_by0
            q22_by1
            q22_layer-name
          )
        )
      )
    )

    (qhpe21:make-text-aligned
      (list
        (- q22_x0 (* 0.40 q22_text-height))
        (+ q22_by0 (/ q22_band-height 2.0))
        0.0
      )
      q22_layer-name
      q22_text-height
      0.0
      2
      2
      q22_annotation-layer
      q22_style
    )
    (setq q22_layer-index (1+ q22_layer-index))
  )

  (princ "\nInserindo linhas e textos das estacas principais...")
  (setq q22_main
    (qhpe21:main-station-distances
      q22_total q22_start-station q22_station-step
    )
  )
  (foreach q22_distance q22_main
    (if (numberp q22_distance)
      (progn
        (setq q22_x (+ q22_x0 q22_distance)
              q22_station-label
                (qhpe:format-station-diagram
                  (+ q22_start-station q22_distance)
                  q22_station-step
                ))
        (qhpe21:make-line
          (list q22_x q22_y0 0.0)
          (list q22_x q22_band-top 0.0)
          q22_annotation-layer
        )
        (qhpe21:make-text-aligned
          (list q22_x q22_label-y 0.0)
          q22_station-label
          q22_text-height
          0.0
          1
          0
          q22_annotation-layer
          q22_style
        )
      )
    )
  )

  (princ "\nInserindo estacas de inicio e fim das descontinuidades...")
  (setq q22_discontinuities
    (qhpe21:discontinuity-distances
      q22_diagram-data q22_main q22_total
    )
  )
  (setq q22_disc-index 0)
  (foreach q22_distance q22_discontinuities
    (if (numberp q22_distance)
      (progn
        (setq q22_x (+ q22_x0 q22_distance)
              q22_disc-level (rem q22_disc-index 2)
              q22_station-label
                (qhpe:format-station-diagram
                  (+ q22_start-station q22_distance)
                  q22_station-step
                )
              ;; Mais comprimento abaixo da linha-base. Os dois niveis
              ;; alternados reduzem a sobreposicao entre estacas proximas.
              q22_disc-line-bottom
                (- q22_y0 (* 7.2 q22_text-height))
              q22_disc-text-y
                (- q22_y0
                  (* (+ 5.85 (* q22_disc-level 0.85)) q22_text-height)))

        (qhpe21:make-line
          (list q22_x q22_disc-line-bottom 0.0)
          (list q22_x q22_band-top 0.0)
          q22_annotation-layer
        )
        (qhpe21:make-text-left
          (list
            (- q22_x (* 0.24 q22_text-height))
            q22_disc-text-y
            0.0
          )
          q22_station-label
          q22_text-height
          (/ pi 2.0)
          q22_annotation-layer
          q22_style
        )
        (setq q22_disc-index (1+ q22_disc-index))
      )
    )
  )
  T
)

(defun qhpe2:draw-base-diagram
  (q22_insertion q22_total q22_layers q22_diagram-data q22_start-station
    q22_station-step q22_band-height q22_text-height q22_annotation-layer)
  (qhpe22:draw-base-diagram
    q22_insertion q22_total q22_layers q22_diagram-data q22_start-station
    q22_station-step q22_band-height q22_text-height q22_annotation-layer
  )
)

(defun qhpe22:text-width (q22_text q22_height q22_style / q22_ext)
  (setq q22_ext (qhpe21:text-extents q22_text q22_height q22_style))
  (if (and (listp q22_ext) (numberp (nth 4 q22_ext)))
    (nth 4 q22_ext)
    (* (strlen q22_text) q22_height 0.62)
  )
)

(defun qhpe22:max-text-width
  (q22_texts q22_height q22_style / q22_max q22_text q22_width)
  (setq q22_max 0.0)
  (foreach q22_text q22_texts
    (setq q22_width (qhpe22:text-width q22_text q22_height q22_style))
    (if (> q22_width q22_max)
      (setq q22_max q22_width)
    )
  )
  q22_max
)

(defun qhpe22:area-table-data
  (q22_row q22_layers q22_accumulated q22_linear-factor
    q22_start-station q22_station-step
    / q22_title q22_rows q22_layer q22_area q22_acc-area)

  (setq q22_title
    (strcat
      (qhpe:format-station-diagram
        (+ q22_start-station (nth 4 q22_row))
        q22_station-step
      )
      " a "
      (qhpe:format-station-diagram
        (+ q22_start-station (nth 5 q22_row))
        q22_station-step
      )
    )
    q22_rows nil)

  (foreach q22_layer q22_layers
    (setq q22_area
      (max 0.0 (qhpe:row-area (nth 3 q22_row) q22_layer))
    )
    (setq q22_acc-area
      (if (assoc q22_layer q22_accumulated)
        (max 0.0 (cdr (assoc q22_layer q22_accumulated)))
        0.0
      )
    )
    (setq q22_rows
      (append
        q22_rows
        (list
          (list
            q22_layer
            (strcat
              (qhpe:format-number
                (* q22_area q22_linear-factor q22_linear-factor)
                2
              )
              " m\\U+00B2"
            )
            (strcat
              (qhpe:format-number
                (* q22_acc-area q22_linear-factor q22_linear-factor)
                2
              )
              " m\\U+00B2"
            )
          )
        )
      )
    )
  )
  (list q22_title q22_rows)
)

(defun qhpe22:table-widths
  (q22_rows q22_height q22_style
    / q22_service-texts q22_area-texts q22_acc-texts q22_row
      q22_pad-x q22_w1 q22_w2 q22_w3)

  (setq q22_service-texts (list "SERVI\\U+00C7O")
        q22_area-texts (list "\\U+00C1REA")
        q22_acc-texts (list "ACUMULADO"))

  (foreach q22_row q22_rows
    (setq q22_service-texts (cons (nth 0 q22_row) q22_service-texts)
          q22_area-texts (cons (nth 1 q22_row) q22_area-texts)
          q22_acc-texts (cons (nth 2 q22_row) q22_acc-texts))
  )

  (setq q22_pad-x (* 0.42 q22_height)
        q22_w1 (+ (qhpe22:max-text-width q22_service-texts q22_height q22_style)
                  (* 2.0 q22_pad-x))
        q22_w2 (+ (qhpe22:max-text-width q22_area-texts q22_height q22_style)
                  (* 2.0 q22_pad-x))
        q22_w3 (+ (qhpe22:max-text-width q22_acc-texts q22_height q22_style)
                  (* 2.0 q22_pad-x)))
  (list q22_w1 q22_w2 q22_w3)
)

(defun qhpe22:draw-area-table
  (q22_mid-x q22_available-width q22_bottom q22_title q22_rows
    q22_preferred-height q22_layer q22_style
    / q22_height q22_widths q22_w1 q22_w2 q22_w3 q22_sum-width
      q22_title-width q22_pad-x q22_extra q22_scale q22_row-height
      q22_row-count q22_table-height q22_left q22_right q22_top
      q22_title-bottom q22_header-bottom q22_x1 q22_x2 q22_y
      q22_row q22_row-top q22_row-bottom q22_center-y)

  (setq q22_height q22_preferred-height
        q22_widths (qhpe22:table-widths q22_rows q22_height q22_style)
        q22_w1 (nth 0 q22_widths)
        q22_w2 (nth 1 q22_widths)
        q22_w3 (nth 2 q22_widths)
        q22_pad-x (* 0.42 q22_height)
        q22_sum-width (+ q22_w1 q22_w2 q22_w3)
        q22_title-width
          (+ (qhpe22:text-width q22_title q22_height q22_style)
             (* 2.0 q22_pad-x)))

  ;; Se o titulo for mais largo, distribui a diferenca entre as colunas.
  (if (> q22_title-width q22_sum-width)
    (progn
      (setq q22_extra (/ (- q22_title-width q22_sum-width) 3.0)
            q22_w1 (+ q22_w1 q22_extra)
            q22_w2 (+ q22_w2 q22_extra)
            q22_w3 (+ q22_w3 q22_extra)
            q22_sum-width (+ q22_w1 q22_w2 q22_w3))
    )
  )

  ;; Reduz proporcionalmente para caber entre as estacas.
  (if (> q22_sum-width (* 0.94 q22_available-width))
    (progn
      (setq q22_scale
        (/ (* 0.94 q22_available-width) q22_sum-width))
      (setq q22_height
        (* q22_preferred-height (max 0.18 (min 1.0 q22_scale))))
      (setq q22_widths (qhpe22:table-widths q22_rows q22_height q22_style)
            q22_w1 (nth 0 q22_widths)
            q22_w2 (nth 1 q22_widths)
            q22_w3 (nth 2 q22_widths)
            q22_pad-x (* 0.42 q22_height)
            q22_sum-width (+ q22_w1 q22_w2 q22_w3)
            q22_title-width
              (+ (qhpe22:text-width q22_title q22_height q22_style)
                 (* 2.0 q22_pad-x)))
      (if (> q22_title-width q22_sum-width)
        (progn
          (setq q22_extra (/ (- q22_title-width q22_sum-width) 3.0)
                q22_w1 (+ q22_w1 q22_extra)
                q22_w2 (+ q22_w2 q22_extra)
                q22_w3 (+ q22_w3 q22_extra)
                q22_sum-width (+ q22_w1 q22_w2 q22_w3))
        )
      )
    )
  )

  (setq q22_row-height (* 1.48 q22_height)
        q22_row-count (+ 2 (length q22_rows))
        q22_table-height (* q22_row-count q22_row-height)
        q22_left (- q22_mid-x (/ q22_sum-width 2.0))
        q22_right (+ q22_left q22_sum-width)
        q22_top (+ q22_bottom q22_table-height)
        q22_title-bottom (- q22_top q22_row-height)
        q22_header-bottom (- q22_title-bottom q22_row-height)
        q22_x1 (+ q22_left q22_w1)
        q22_x2 (+ q22_x1 q22_w2))

  ;; Contorno externo e divisorias horizontais.
  (qhpe21:make-rectangle q22_left q22_bottom q22_right q22_top q22_layer)
  (setq q22_y (- q22_top q22_row-height))
  (repeat (1- q22_row-count)
    (qhpe21:make-line
      (list q22_left q22_y 0.0)
      (list q22_right q22_y 0.0)
      q22_layer
    )
    (setq q22_y (- q22_y q22_row-height))
  )

  ;; Divisorias verticais abaixo do titulo mesclado.
  (qhpe21:make-line
    (list q22_x1 q22_bottom 0.0)
    (list q22_x1 q22_title-bottom 0.0)
    q22_layer
  )
  (qhpe21:make-line
    (list q22_x2 q22_bottom 0.0)
    (list q22_x2 q22_title-bottom 0.0)
    q22_layer
  )

  ;; Titulo.
  (qhpe21:make-text-aligned
    (list q22_mid-x (- q22_top (/ q22_row-height 2.0)) 0.0)
    q22_title q22_height 0.0 1 2 q22_layer q22_style
  )

  ;; Cabecalhos.
  (setq q22_center-y (- q22_title-bottom (/ q22_row-height 2.0)))
  (qhpe21:make-text-aligned
    (list (+ q22_left (/ q22_w1 2.0)) q22_center-y 0.0)
    "SERVI\\U+00C7O" q22_height 0.0 1 2 q22_layer q22_style
  )
  (qhpe21:make-text-aligned
    (list (+ q22_x1 (/ q22_w2 2.0)) q22_center-y 0.0)
    "\\U+00C1REA" q22_height 0.0 1 2 q22_layer q22_style
  )
  (qhpe21:make-text-aligned
    (list (+ q22_x2 (/ q22_w3 2.0)) q22_center-y 0.0)
    "ACUMULADO" q22_height 0.0 1 2 q22_layer q22_style
  )

  ;; Linhas de dados.
  (setq q22_row-top q22_header-bottom)
  (foreach q22_row q22_rows
    (setq q22_row-bottom (- q22_row-top q22_row-height)
          q22_center-y (/ (+ q22_row-top q22_row-bottom) 2.0))

    (qhpe21:make-text-aligned
      (list (+ q22_left q22_pad-x) q22_center-y 0.0)
      (nth 0 q22_row) q22_height 0.0 0 2 q22_layer q22_style
    )
    (qhpe21:make-text-aligned
      (list (+ q22_x1 (/ q22_w2 2.0)) q22_center-y 0.0)
      (nth 1 q22_row) q22_height 0.0 1 2 q22_layer q22_style
    )
    (qhpe21:make-text-aligned
      (list (+ q22_x2 (/ q22_w3 2.0)) q22_center-y 0.0)
      (nth 2 q22_row) q22_height 0.0 1 2 q22_layer q22_style
    )
    (setq q22_row-top q22_row-bottom)
  )
  T
)

(defun qhpe22:draw-area-tables
  (q22_insertion q22_rows q22_layers q22_start-station q22_station-step
    q22_linear-factor q22_band-height q22_text-height q22_annotation-layer
    / q22_x0 q22_y0 q22_layer-count q22_band-base q22_band-top
      q22_accumulated q22_row q22_layer q22_current-area q22_d0 q22_d1
      q22_mid-x q22_available-width q22_table-bottom q22_table-data
      q22_title q22_data-rows q22_style)

  (setq q22_x0 (car q22_insertion)
        q22_y0 (cadr q22_insertion)
        q22_layer-count (length q22_layers)
        q22_style (qhpe21:ensure-romans-style)
        q22_band-base
          (+
            q22_y0
            (max (* 2.35 q22_text-height) (* 0.08 q22_station-step))
          )
        q22_band-top
          (+ q22_band-base (* q22_layer-count q22_band-height))
        q22_accumulated nil)

  (princ "\nInserindo tabelas de areas centralizadas entre as estacas...")
  (foreach q22_row q22_rows
    (foreach q22_layer q22_layers
      (setq q22_current-area
        (max 0.0 (qhpe:row-area (nth 3 q22_row) q22_layer))
      )
      (setq q22_accumulated
        (qhpe:update-accumulated
          q22_accumulated q22_layer q22_current-area
        )
      )
    )

    (setq q22_d0 (nth 4 q22_row)
          q22_d1 (nth 5 q22_row))
    (if
      (and
        (numberp q22_d0)
        (numberp q22_d1)
        (> q22_d1 q22_d0)
      )
      (progn
        (setq q22_mid-x (+ q22_x0 (/ (+ q22_d0 q22_d1) 2.0))
              q22_available-width
                (max (* 2.0 q22_text-height)
                  (- q22_d1 q22_d0 (* 0.35 q22_text-height)))
              q22_table-bottom (+ q22_band-top (* 0.70 q22_text-height))
              q22_table-data
                (qhpe22:area-table-data
                  q22_row q22_layers q22_accumulated q22_linear-factor
                  q22_start-station q22_station-step
                )
              q22_title (car q22_table-data)
              q22_data-rows (cadr q22_table-data))

        (qhpe22:draw-area-table
          q22_mid-x q22_available-width q22_table-bottom
          q22_title q22_data-rows q22_text-height
          q22_annotation-layer q22_style
        )
      )
    )
  )
  T
)

(defun qhpe2:draw-area-boxes
  (q22_insertion q22_rows q22_layers q22_start-station q22_station-step
    q22_linear-factor q22_band-height q22_text-height q22_annotation-layer)
  (qhpe22:draw-area-tables
    q22_insertion q22_rows q22_layers q22_start-station q22_station-step
    q22_linear-factor q22_band-height q22_text-height q22_annotation-layer
  )
)

;;; ================================================================
;;;
;;;   - tabelas geradas como objeto TABLE nativo do AutoCAD;
;;;   - margens horizontal e vertical configuradas no objeto TABLE;
;;;   - duas faixas estreitas para MEIO-FIO RETO e MEIO-FIO CURVO;
;;;   - quantificacao linear por estaca e acumulada, em metros;
;;;   - identificacao de trechos retos/curvos pelos segmentos da polilinha:
;;;       bulge = 0   -> reto
;;;       bulge <> 0  -> curvo
;;;   - aceita LWPOLYLINE, POLYLINE, LINE e ARC no layer escolhido.
;;; ================================================================

(setq qhpe:*version* "2.4")
(setq qhpe23:*curb-layer-default* nil)
(setq qhpe23:*curb-label-straight* "MEIO-FIO RETO")
(setq qhpe23:*curb-label-curved* "MEIO-FIO CURVO")
(setq qhpe23:acMiddleLeft 4)
(setq qhpe23:acMiddleCenter 5)
(setq qhpe23:acMiddleRight 6)

(defun qhpe23:entity-curve-p (ent / typ)
  (if ent
    (progn
      (setq typ (cdr (assoc 0 (entget ent))))
      (member typ '("LWPOLYLINE" "POLYLINE" "LINE" "ARC"))
    )
  )
)

(defun qhpe23:prompt-curb-layer (message / selection ent layer)
  (setq selection (entsel message))
  (if selection
    (progn
      (setq ent (car selection))
      (if (qhpe23:entity-curve-p ent)
        (progn
          (setq layer (cdr (assoc 8 (entget ent))))
          (setq qhpe23:*curb-layer-default* layer)
          layer
        )
        (progn
          (princ "\nO objeto selecionado nao e LINE, ARC ou polilinha valida.")
          (qhpe23:prompt-curb-layer message)
        )
      )
    )
    nil
  )
)

(defun qhpe23:curb-selection-set (layer / filter result)
  (if layer
    (progn
      (setq filter
        (list
          (cons 0 "LWPOLYLINE,POLYLINE,LINE,ARC")
          (cons 8 layer)
          (cons 410 (getvar "CTAB"))
        )
      )
      (setq result (ssget "_X" filter))
      result
    )
  )
)

(defun qhpe23:station-ranges
  (total start-station station-step / d0 d1 station-rem first-length seg rows)
  (setq d0 0.0
        station-rem
          (- start-station
             (* (fix (/ start-station station-step)) station-step)))
  (if (< (abs station-rem) (* station-step 1.0e-8))
    (setq first-length station-step)
    (setq first-length (- station-step station-rem))
  )
  (setq seg first-length)
  (while (< d0 (- total 1.0e-8))
    (setq d1 (min total (+ d0 seg)))
    (setq rows (cons (list d0 d1) rows))
    (setq d0 d1
          seg station-step)
  )
  (reverse rows)
)

(defun qhpe23:range-index (ranges distance / index item found tol)
  (setq index 0
        tol 1.0e-8)
  (while (and ranges (null found))
    (setq item (car ranges))
    (if
      (and
        (>= distance (- (car item) tol))
        (or
          (< distance (- (cadr item) tol))
          (and (null (cdr ranges)) (<= distance (+ (cadr item) tol)))
        )
      )
      (setq found index)
      (progn
        (setq ranges (cdr ranges))
        (setq index (1+ index))
      )
    )
  )
  found
)

(defun qhpe23:add-curb-stat
  (stats index kind length / item straight curved replacement)
  (setq item (assoc index stats))
  (if item
    (setq straight (cadr item)
          curved (caddr item))
    (setq straight 0.0
          curved 0.0)
  )
  (if (= kind 'CURVO)
    (setq curved (+ curved length))
    (setq straight (+ straight length))
  )
  (setq replacement (list index straight curved))
  (if item
    (subst replacement item stats)
    (cons replacement stats)
  )
)

(defun qhpe23:safe-dist-at-param (curve parameter / result)
  (setq result
    (vl-catch-all-apply
      'vlax-curve-getDistAtParam
      (list curve parameter)
    )
  )
  (if (vl-catch-all-error-p result) nil result)
)

(defun qhpe23:polyline-segments
  (ent / obj end-param index d0 d1 bulge bulge-result segments)
  (setq obj (qhpe:safe-vla-object ent)
        end-param
          (vl-catch-all-apply 'vlax-curve-getEndParam (list ent)))
  (if (and obj (not (vl-catch-all-error-p end-param)))
    (progn
      (setq index 0)
      (while (< index (- end-param 1.0e-9))
        (setq d0 (qhpe23:safe-dist-at-param ent (float index))
              d1
                (qhpe23:safe-dist-at-param
                  ent
                  (min end-param (+ (float index) 1.0))
                )
              bulge-result
                (vl-catch-all-apply 'vla-GetBulge (list obj index)))
        (if (vl-catch-all-error-p bulge-result)
          (setq bulge 0.0)
          (setq bulge bulge-result)
        )
        (if (and (numberp d0) (numberp d1) (> d1 (+ d0 1.0e-10)))
          (setq segments
            (cons
              (list
                (if (> (abs bulge) 1.0e-10) 'CURVO 'RETO)
                d0
                d1
              )
              segments
            )
          )
        )
        (setq index (1+ index))
      )
    )
  )
  (reverse segments)
)

(defun qhpe23:entity-segments (ent / typ length)
  (setq typ (cdr (assoc 0 (entget ent))))
  (cond
    ((= typ "ARC")
      (setq length (qhpe:curve-length ent))
      (if (and (numberp length) (> length 1.0e-10))
        (list (list 'CURVO 0.0 length))
      )
    )
    ((= typ "LINE")
      (setq length (qhpe:curve-length ent))
      (if (and (numberp length) (> length 1.0e-10))
        (list (list 'RETO 0.0 length))
      )
    )
    ((member typ '("LWPOLYLINE" "POLYLINE"))
      (qhpe23:polyline-segments ent)
    )
  )
)

(defun qhpe23:project-at-curve-distance (axis curve distance / point)
  (setq point (qhpe:safe-curve-point-at-dist curve distance))
  (if point
    (qhpe:project-point-to-axis-distance axis point)
  )
)

(defun qhpe23:allocate-curb-piece
  (axis total ent kind a b ranges stats intervals depth
    / midpoint pa pm pb ia im ib target piece-length lo hi left-result
      right-result)
  (setq midpoint (/ (+ a b) 2.0)
        pa (qhpe23:project-at-curve-distance axis ent a)
        pm (qhpe23:project-at-curve-distance axis ent midpoint)
        pb (qhpe23:project-at-curve-distance axis ent b)
        piece-length (- b a)
        ia (if (numberp pa)
             (qhpe23:range-index ranges (qhpe:clamp pa 0.0 total)))
        im (if (numberp pm)
             (qhpe23:range-index ranges (qhpe:clamp pm 0.0 total)))
        ib (if (numberp pb)
             (qhpe23:range-index ranges (qhpe:clamp pb 0.0 total))))

  ;; Se inicio, meio e fim caem na mesma estaca, a parcela pode ser
  ;; contabilizada integralmente. Proximo das divisas, divide por busca
  ;; binaria ate aproximadamente 1 mm nas unidades do desenho.
  (if
    (or
      (and (numberp ia) (numberp im) (numberp ib) (= ia im) (= im ib))
      (>= depth 16)
      (< piece-length 0.001)
    )
    (progn
      (setq target
        (cond ((numberp im) im) ((numberp ia) ia) ((numberp ib) ib)))
      (if (numberp target)
        (setq stats
          (qhpe23:add-curb-stat stats target kind piece-length))
      )
      (if (and (numberp pa) (numberp pm) (numberp pb))
        (progn
          (setq lo (max 0.0 (min total (min pa pm pb)))
                hi (max 0.0 (min total (max pa pm pb))))
          (if (> (- hi lo) 1.0e-7)
            (setq intervals (cons (list lo hi) intervals))
          )
        )
      )
      (list stats intervals)
    )
    (progn
      (setq left-result
        (qhpe23:allocate-curb-piece
          axis total ent kind a midpoint ranges stats intervals (1+ depth)
        )
        stats (car left-result)
        intervals (cadr left-result)
        right-result
          (qhpe23:allocate-curb-piece
            axis total ent kind midpoint b ranges stats intervals (1+ depth)
          )
      )
      (list (car right-result) (cadr right-result))
    )
  )
)

(defun qhpe23:sample-segment
  (axis total ent kind seg-start seg-end ranges sample-step stats intervals
    / current next result)
  (setq current seg-start)
  (while (< current (- seg-end 1.0e-10))
    (setq next (min seg-end (+ current sample-step))
          result
            (qhpe23:allocate-curb-piece
              axis total ent kind current next ranges stats intervals 0
            )
          stats (car result)
          intervals (cadr result)
          current next)
  )
  (list stats intervals)
)

(defun qhpe23:analyze-curbs
  (axis total selection-set ranges station-step
    / sample-step counter ent segments segment kind d0 d1 result stats
      straight-intervals curved-intervals interval-list index item rows
      straight curved total-straight total-curved)

  (setq sample-step (min 0.25 (max 0.05 (/ station-step 100.0)))
        counter 0
        stats nil
        straight-intervals nil
        curved-intervals nil)

  (if selection-set
    (repeat (sslength selection-set)
      (setq ent (ssname selection-set counter)
            segments (qhpe23:entity-segments ent))
      (foreach segment segments
        (setq kind (car segment)
              d0 (cadr segment)
              d1 (caddr segment)
              interval-list
                (if (= kind 'CURVO) curved-intervals straight-intervals)
              result
                (qhpe23:sample-segment
                  axis total ent kind d0 d1 ranges sample-step stats interval-list
                )
              stats (car result)
              interval-list (cadr result))
        (if (= kind 'CURVO)
          (setq curved-intervals interval-list)
          (setq straight-intervals interval-list)
        )
      )
      (setq counter (1+ counter))
    )
  )

  (setq index 0
        total-straight 0.0
        total-curved 0.0)
  (foreach interval ranges
    (setq item (assoc index stats)
          straight (if item (cadr item) 0.0)
          curved (if item (caddr item) 0.0)
          total-straight (+ total-straight straight)
          total-curved (+ total-curved curved)
          rows (cons (list (car interval) (cadr interval) straight curved) rows)
          index (1+ index))
  )

  (list
    (reverse rows)
    (qhpe:merge-intervals straight-intervals 0.02)
    (qhpe:merge-intervals curved-intervals 0.02)
    total-straight
    total-curved
  )
)

(defun qhpe23:curb-row-value (row kind)
  (if row
    (if (= kind 'CURVO) (nth 3 row) (nth 2 row))
    0.0
  )
)

(defun qhpe23:curb-height (band-height text-height)
  (max (* 0.55 text-height) (* 0.20 band-height))
)

(defun qhpe23:diagram-geometry
  (insertion station-step pavement-count band-height text-height has-curb
    / y0 band-base curb-height curb-total pavement-base band-top)
  (setq y0 (cadr insertion)
        band-base
          (+ y0 (max (* 2.35 text-height) (* 0.08 station-step)))
        curb-height (if has-curb (qhpe23:curb-height band-height text-height) 0.0)
        curb-total (if has-curb (* 2.0 curb-height) 0.0)
        pavement-base (+ band-base curb-total)
        band-top (+ pavement-base (* pavement-count band-height)))
  (list band-base curb-height pavement-base band-top)
)

(defun qhpe23:draw-base-diagram
  (insertion total layers diagram-data start-station station-step band-height
    text-height annotation-layer curb-layer curb-straight curb-curved
    / x0 y0 layer-count has-curb geometry band-base curb-height pavement-base
      band-top style index layer-name layer-data occurrence by0 by1 main
      distance x label-y station-label combined-data discontinuities disc-index
      disc-level disc-bottom disc-text-y straight-y0 straight-y1 curved-y0
      curved-y1)

  (setq x0 (car insertion)
        y0 (cadr insertion)
        layer-count (length layers)
        has-curb (if curb-layer T nil)
        geometry
          (qhpe23:diagram-geometry
            insertion station-step layer-count band-height text-height has-curb
          )
        band-base (nth 0 geometry)
        curb-height (nth 1 geometry)
        pavement-base (nth 2 geometry)
        band-top (nth 3 geometry)
        style (qhpe21:ensure-romans-style)
        label-y (- y0 (* 1.50 text-height)))

  (princ "\nDesenhando as faixas dos pavimentos...")
  (setq index 0)
  (foreach layer-name layers
    (setq by0
      (+ pavement-base (* (- layer-count index 1) band-height))
    )
    (setq by1 (+ by0 band-height)
          layer-data (assoc layer-name diagram-data))
    (if layer-data
      (foreach occurrence (cdr layer-data)
        (if
          (and
            (listp occurrence)
            (numberp (car occurrence))
            (numberp (cadr occurrence))
            (> (- (cadr occurrence) (car occurrence)) 1.0e-8)
          )
          (qhpe21:make-band
            (+ x0 (car occurrence)) (+ x0 (cadr occurrence))
            by0 by1 layer-name
          )
        )
      )
    )
    (qhpe21:make-text-aligned
      (list (- x0 (* 0.40 text-height)) (+ by0 (/ band-height 2.0)) 0.0)
      layer-name text-height 0.0 2 2 annotation-layer style
    )
    (setq index (1+ index))
  )

  (if has-curb
    (progn
      ;; As duas faixas de meio-fio ficam mais estreitas e encostadas.
      ;; Ordem vertical: RETO acima e CURVO abaixo.
      (setq curved-y0 band-base
            curved-y1 (+ curved-y0 curb-height)
            straight-y0 curved-y1
            straight-y1 (+ straight-y0 curb-height))

      (foreach occurrence curb-curved
        (qhpe21:make-band
          (+ x0 (car occurrence)) (+ x0 (cadr occurrence))
          curved-y0 curved-y1 curb-layer
        )
      )
      (foreach occurrence curb-straight
        (qhpe21:make-band
          (+ x0 (car occurrence)) (+ x0 (cadr occurrence))
          straight-y0 straight-y1 curb-layer
        )
      )
      (qhpe21:make-text-aligned
        (list (- x0 (* 0.40 text-height))
              (+ straight-y0 (/ curb-height 2.0)) 0.0)
        qhpe23:*curb-label-straight* (* 0.86 text-height)
        0.0 2 2 annotation-layer style
      )
      (qhpe21:make-text-aligned
        (list (- x0 (* 0.40 text-height))
              (+ curved-y0 (/ curb-height 2.0)) 0.0)
        qhpe23:*curb-label-curved* (* 0.86 text-height)
        0.0 2 2 annotation-layer style
      )
    )
  )

  (princ "\nInserindo linhas e textos das estacas principais...")
  (setq main
    (qhpe21:main-station-distances total start-station station-step)
  )
  (foreach distance main
    (if (numberp distance)
      (progn
        (setq x (+ x0 distance)
              station-label
                (qhpe:format-station-diagram
                  (+ start-station distance) station-step
                ))
        (qhpe21:make-line
          (list x y0 0.0) (list x band-top 0.0) annotation-layer
        )
        (qhpe21:make-text-aligned
          (list x label-y 0.0) station-label text-height 0.0
          1 0 annotation-layer style
        )
      )
    )
  )

  (setq combined-data diagram-data)
  (if has-curb
    (setq combined-data
      (append
        combined-data
        (list
          (cons qhpe23:*curb-label-straight* curb-straight)
          (cons qhpe23:*curb-label-curved* curb-curved)
        )
      )
    )
  )

  (princ "\nInserindo estacas de inicio e fim das ocorrencias...")
  (setq discontinuities
    (qhpe21:discontinuity-distances combined-data main total)
        disc-index 0)
  (foreach distance discontinuities
    (if (numberp distance)
      (progn
        (setq x (+ x0 distance)
              disc-level (rem disc-index 2)
              station-label
                (qhpe:format-station-diagram
                  (+ start-station distance) station-step
                )
              disc-bottom (- y0 (* 8.40 text-height))
              disc-text-y
                (- y0 (* (+ 6.75 (* disc-level 1.00)) text-height)))
        (qhpe21:make-line
          (list x disc-bottom 0.0) (list x band-top 0.0) annotation-layer
        )
        (qhpe21:make-text-left
          (list (- x (* 0.24 text-height)) disc-text-y 0.0)
          station-label text-height (/ pi 2.0) annotation-layer style
        )
        (setq disc-index (1+ disc-index))
      )
    )
  )
  T
)

(defun qhpe23:table-data
  (area-row layers area-accum curb-row curb-accum-straight curb-accum-curved
    linear-factor start-station station-step include-curb
    / title rows layer area accumulated straight curved)
  (setq title
    (strcat
      (qhpe:format-station-diagram
        (+ start-station (nth 4 area-row)) station-step
      )
      " a "
      (qhpe:format-station-diagram
        (+ start-station (nth 5 area-row)) station-step
      )
    )
    rows nil)

  (foreach layer layers
    (setq area (max 0.0 (qhpe:row-area (nth 3 area-row) layer))
          accumulated
            (if (assoc layer area-accum)
              (max 0.0 (cdr (assoc layer area-accum)))
              0.0
            ))
    (setq rows
      (append rows
        (list
          (list
            layer
            (strcat
              (qhpe:format-number
                (* area linear-factor linear-factor) 2
              )
              " m\\U+00B2"
            )
            (strcat
              (qhpe:format-number
                (* accumulated linear-factor linear-factor) 2
              )
              " m\\U+00B2"
            )
          )
        )
      )
    )
  )

  (if include-curb
    (progn
      (setq straight (qhpe23:curb-row-value curb-row 'RETO)
            curved (qhpe23:curb-row-value curb-row 'CURVO))
      (setq rows
        (append rows
          (list
            (list
              qhpe23:*curb-label-straight*
              (strcat
                (qhpe:format-number (* straight linear-factor) 2) " m"
              )
              (strcat
                (qhpe:format-number (* curb-accum-straight linear-factor) 2)
                " m"
              )
            )
            (list
              qhpe23:*curb-label-curved*
              (strcat
                (qhpe:format-number (* curved linear-factor) 2) " m"
              )
              (strcat
                (qhpe:format-number (* curb-accum-curved linear-factor) 2)
                " m"
              )
            )
          )
        )
      )
    )
  )
  (list title rows)
)

(defun qhpe23:table-widths
  (rows height style / service-texts quantity-texts accum-texts row
    margin-x w1 w2 w3)
  (setq service-texts (list "SERVI\\U+00C7O")
        quantity-texts (list "QUANTIDADE")
        accum-texts (list "ACUMULADO"))
  (foreach row rows
    (setq service-texts (cons (nth 0 row) service-texts)
          quantity-texts (cons (nth 1 row) quantity-texts)
          accum-texts (cons (nth 2 row) accum-texts))
  )
  (setq margin-x (* 0.46 height)
        w1 (+ (qhpe22:max-text-width service-texts height style)
              (* 2.0 margin-x))
        w2 (+ (qhpe22:max-text-width quantity-texts height style)
              (* 2.0 margin-x))
        w3 (+ (qhpe22:max-text-width accum-texts height style)
              (* 2.0 margin-x)))
  (list w1 w2 w3)
)

(defun qhpe23:fit-table
  (title rows preferred-height available-width style
    / height widths w1 w2 w3 total title-width margin-x extra scale)
  (setq height preferred-height)
  (repeat 2
    (setq widths (qhpe23:table-widths rows height style)
          w1 (nth 0 widths)
          w2 (nth 1 widths)
          w3 (nth 2 widths)
          margin-x (* 0.46 height)
          total (+ w1 w2 w3)
          title-width
            (+ (qhpe22:text-width title height style) (* 2.0 margin-x)))
    (if (> title-width total)
      (progn
        (setq extra (/ (- title-width total) 3.0)
              w1 (+ w1 extra)
              w2 (+ w2 extra)
              w3 (+ w3 extra)
              total (+ w1 w2 w3))
      )
    )
    (if (> total (* 0.94 available-width))
      (progn
        (setq scale (/ (* 0.94 available-width) total)
              height (* height (max 0.18 (min 1.0 scale))))
      )
    )
  )
  (list height w1 w2 w3)
)

(defun qhpe23:safe-table-call (function arguments)
  (vl-catch-all-apply function arguments)
)

(defun qhpe23:create-native-table
  (mid-x available-width bottom title rows preferred-height layer style
    / acad doc space fit height w1 w2 w3 total-width margin-x margin-y
      title-height row-height row-count table-height top-left table row-index
      data-row row)

  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        fit
          (qhpe23:fit-table
            title rows preferred-height available-width style
          )
        height (nth 0 fit)
        w1 (nth 1 fit)
        w2 (nth 2 fit)
        w3 (nth 3 fit)
        total-width (+ w1 w2 w3)
        margin-x (* 0.46 height)
        margin-y (* 0.24 height)
        title-height (+ (* 1.25 height) (* 2.0 margin-y))
        row-height (+ (* 1.10 height) (* 2.0 margin-y))
        row-count (+ 2 (length rows))
        table-height (+ title-height (* (1- row-count) row-height))
        top-left
          (list (- mid-x (/ total-width 2.0)) (+ bottom table-height) 0.0))

  (setq table
    (vla-AddTable
      space (vlax-3d-point top-left) row-count 3 row-height w1
    )
  )
  (if table
    (progn
      (qhpe23:safe-table-call 'vla-put-Layer (list table layer))
      (qhpe27:apply-table-style table)
      (qhpe23:safe-table-call
        'vla-put-RegenerateTableSuppressed (list table :vlax-true))
      (qhpe23:safe-table-call 'vla-put-HorzCellMargin (list table margin-x))
      (qhpe23:safe-table-call 'vla-put-VertCellMargin (list table margin-y))
      (qhpe23:safe-table-call 'vla-put-TitleSuppressed (list table :vlax-false))
      (qhpe23:safe-table-call 'vla-put-HeaderSuppressed (list table :vlax-false))

      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 0 w1))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 1 w2))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 2 w3))
      (qhpe23:safe-table-call 'vla-SetRowHeight (list table 0 title-height))
      (setq row-index 1)
      (while (< row-index row-count)
        (qhpe23:safe-table-call
          'vla-SetRowHeight (list table row-index row-height))
        (setq row-index (1+ row-index))
      )

      (qhpe23:safe-table-call 'vla-MergeCells (list table 0 0 0 2))
      (qhpe23:safe-table-call
        'vla-SetTextStyle
        (list table (+ qhpe:acTitleRow qhpe:acHeaderRow qhpe:acDataRow) style)
      )
      (qhpe23:safe-table-call
        'vla-SetTextHeight
        (list table (+ qhpe:acTitleRow qhpe:acHeaderRow qhpe:acDataRow) height)
      )

      (qhpe:set-table-text table 0 0 title)
      (qhpe:set-table-text table 1 0 "SERVI\\U+00C7O")
      (qhpe:set-table-text table 1 1 "QUANTIDADE")
      (qhpe:set-table-text table 1 2 "ACUMULADO")
      (qhpe23:safe-table-call
        'vla-SetCellAlignment (list table 0 0 qhpe23:acMiddleCenter))
      (setq row-index 0)
      (while (< row-index 3)
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table 1 row-index qhpe23:acMiddleCenter))
        (setq row-index (1+ row-index))
      )

      (setq data-row 2)
      (foreach row rows
        (qhpe:set-table-text table data-row 0 (nth 0 row))
        (qhpe:set-table-text table data-row 1 (nth 1 row))
        (qhpe:set-table-text table data-row 2 (nth 2 row))
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table data-row 0 qhpe23:acMiddleLeft))
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table data-row 1 qhpe23:acMiddleRight))
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table data-row 2 qhpe23:acMiddleRight))
        (setq data-row (1+ data-row))
      )

      (qhpe23:safe-table-call
        'vla-put-RegenerateTableSuppressed (list table :vlax-false))
      (qhpe23:safe-table-call 'vla-RecomputeTableBlock (list table :vlax-true))
      (qhpe23:safe-table-call 'vla-Update (list table))
    )
  )
  table
)

(defun qhpe23:draw-native-tables
  (insertion area-rows layers curb-rows start-station station-step linear-factor
    band-height text-height annotation-layer include-curb
    / x0 y0 geometry band-top area-accum curb-accum-straight
      curb-accum-curved index area-row curb-row layer current-area d0 d1 mid-x
      available-width table-bottom table-data title data-rows style)

  (setq x0 (car insertion)
        y0 (cadr insertion)
        geometry
          (qhpe23:diagram-geometry
            insertion station-step (length layers) band-height text-height
            include-curb
          )
        band-top (nth 3 geometry)
        area-accum nil
        curb-accum-straight 0.0
        curb-accum-curved 0.0
        index 0
        style (qhpe21:ensure-romans-style))

  (princ "\nInserindo objetos TABLE centralizados entre as estacas...")
  (foreach area-row area-rows
    (foreach layer layers
      (setq current-area
        (max 0.0 (qhpe:row-area (nth 3 area-row) layer)))
      (setq area-accum
        (qhpe:update-accumulated area-accum layer current-area))
    )
    (setq curb-row (if include-curb (nth index curb-rows) nil))
    (if include-curb
      (setq curb-accum-straight
              (+ curb-accum-straight (qhpe23:curb-row-value curb-row 'RETO))
            curb-accum-curved
              (+ curb-accum-curved (qhpe23:curb-row-value curb-row 'CURVO)))
    )

    (setq d0 (nth 4 area-row)
          d1 (nth 5 area-row))
    (if (and (numberp d0) (numberp d1) (> d1 d0))
      (progn
        (setq mid-x (+ x0 (/ (+ d0 d1) 2.0))
              available-width
                (max (* 2.0 text-height)
                     (- d1 d0 (* 0.35 text-height)))
              table-bottom band-top
              table-data
                (qhpe23:table-data
                  area-row layers area-accum curb-row
                  curb-accum-straight curb-accum-curved linear-factor
                  start-station station-step include-curb
                )
              title (car table-data)
              data-rows (cadr table-data))
        (qhpe23:create-native-table
          mid-x available-width table-bottom title data-rows
          text-height annotation-layer style
        )
      )
    )
    (setq index (1+ index))
  )
  T
)

(defun qhpe23:restore-environment
  (doc old-clayer old-cmdecho old-osmode old-error)
  (qhpe:safe-set-clayer old-clayer)
  (vl-catch-all-apply 'setvar (list "CMDECHO" old-cmdecho))
  (vl-catch-all-apply 'setvar (list "OSMODE" old-osmode))
  (vl-catch-all-apply 'vla-EndUndoMark (list doc))
  (setq *error* old-error)
  T
)

(defun c:QHPED
  (/ *error* old-error acad doc space old-cmdecho old-osmode old-clayer
    temp-layer axis-selection axis total hatch-ss station-step station-text
    start-station search-width insertion text-height band-height counter hatch hatch-records
    records layers projection-result projected diagram-data curb-layer curb-ss
    ranges curb-analysis curb-rows curb-straight curb-curved draw-result)

  (setq old-error *error*
        acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        old-cmdecho (getvar "CMDECHO")
        old-osmode (getvar "OSMODE")
        old-clayer (getvar "CLAYER"))

  (defun *error* (message)
    (qhpe:safe-set-clayer qhpe:*diagram-layer-name*)
    (qhpe:cleanup-temp)
    (qhpe23:restore-environment
      doc old-clayer old-cmdecho old-osmode old-error)
    (if
      (and message
        (not (wcmatch (strcase message) "*CANCEL*,*QUIT*,*EXIT*")))
      (princ (strcat "\nErro QHPED: " message)))
    (princ)
  )

  (vla-StartUndoMark doc)
  (setvar "CMDECHO" 0)
  (setvar "OSMODE" 0)
  (setq temp-layer (qhpe:prepare-temp-layer doc))
  (qhpe:get-layer-object doc qhpe:*diagram-layer-name*)

  (princ "\nQHPED - Diagrama de pavimentos e meios-fios")
  (while
    (progn
      (setq axis-selection (entsel "\nSelecione a polilinha do eixo: "))
      (and axis-selection
        (not (qhpe:valid-axis-polyline-p (car axis-selection))))
    )
  )
  (if (null axis-selection)
    (*error* "Polilinha nao selecionada.")
    (progn
      (setq axis (car axis-selection)
            total (qhpe:curve-length axis))
      (if (not (and (numberp total) (> total 1.0e-8)))
        (*error* "A polilinha selecionada nao possui comprimento valido.")
        (progn
          (princ (strcat "\nComprimento geometrico: " (rtos total 2 3)))
          (princ "\nSelecione as hachuras que devem entrar no diagrama:")
          (setq hatch-ss (ssget "_:L" '((0 . "HATCH"))))
          (if (null hatch-ss)
            (*error* "Nenhuma hachura selecionada.")
            (progn
              (setq curb-layer
                (qhpe23:prompt-curb-layer
                  "\nSelecione uma polilinha do layer dos meios-fios ou Enter para ignorar: "
                ))
              (setq curb-ss (qhpe23:curb-selection-set curb-layer))
              (if (and curb-layer (null curb-ss))
                (princ "\nNenhum meio-fio foi encontrado no layer selecionado."))

              (setq station-step
                (qhpe:prompt-real-default
                  "\nComprimento de cada estaca nas unidades do desenho"
                  qhpe:*interval-default*))
              (setq qhpe:*interval-default* station-step)
              (setq station-text
                (getstring T "\nEstaca no inicio da polilinha <0+0.00>: "))
              (if (= station-text "") (setq station-text "0+0.00"))
              (setq start-station
                (qhpe:parse-station station-text station-step))
              (setq search-width
                (qhpe:prompt-real-default
                  "\nLargura de busca para o lado ESQUERDO da polilinha"
                  qhpe:*width-default*))
              (setq qhpe:*width-default* search-width
                    qhpe:*right-width-current*
                      (qhpe:prompt-real-default
                        "\nLargura de busca para o lado DIREITO da polilinha"
                        (if (and (numberp qhpe:*right-width-default*) (> qhpe:*right-width-default* 0.0))
                          qhpe:*right-width-default* search-width))
                    qhpe:*right-width-default* qhpe:*right-width-current*)
              (setq insertion
                (getpoint "\nPonto inicial do diagrama, na linha inferior: "))
              (if (null insertion)
                (*error* "Ponto de insercao nao informado.")
                (progn
                  (if (null qhpe:*diagram-text-height-default*)
                    (setq qhpe:*diagram-text-height-default*
                      (* station-step 0.025)))
                  (setq text-height
                    (qhpe:prompt-real-default
                      "\nAltura dos textos do diagrama"
                      qhpe:*diagram-text-height-default*))
                  (setq qhpe:*diagram-text-height-default* text-height)
                  (if (null qhpe:*diagram-band-height-default*)
                    (setq qhpe:*diagram-band-height-default*
                      (* station-step 0.12)))
                  (setq band-height
                    (qhpe:prompt-real-default
                      "\nAltura de cada faixa de pavimento"
                      qhpe:*diagram-band-height-default*))
                  (setq qhpe:*diagram-band-height-default* band-height)

                  (princ "\nGerando os limites das hachuras...")
                  (setq counter 0 records nil)
                  (repeat (sslength hatch-ss)
                    (setq hatch (ssname hatch-ss counter)
                          hatch-records
                            (qhpe:make-hatch-records hatch space))
                    (if hatch-records
                      (setq records (append hatch-records records)))
                    (setq counter (1+ counter)))

                  (if (null records)
                    (*error* "Nenhum limite de hachura valido foi criado.")
                    (progn
                      (setq layers (qhpe:unique-layers records)
                            layers
                              (qhpe2:sort-layers-from-records layers records))
                      (princ "\nCalculando os inicios e finais das ocorrencias...")
                      (setq projection-result
                        (vl-catch-all-apply
                          'qhpe:projection-intervals-by-layer
                          (list axis total records station-step)))
                      (if (vl-catch-all-error-p projection-result)
                        (*error*
                          (strcat "Falha ao localizar as ocorrencias: "
                            (vl-catch-all-error-message projection-result)))
                        (setq projected projection-result))
                      (setq diagram-data
                        (qhpe2:merged-projections layers projected))

                      (setq ranges
                        (qhpe23:station-ranges
                          total start-station station-step))
                      (if curb-ss
                        (progn
                          (princ "\nQuantificando segmentos retos e curvos dos meios-fios...")
                          (setq curb-analysis
                            (qhpe23:analyze-curbs
                              axis total curb-ss ranges station-step search-width)
                                curb-rows (nth 0 curb-analysis)
                                curb-straight (nth 1 curb-analysis)
                                curb-curved (nth 2 curb-analysis))
                        )
                        (setq curb-rows nil curb-straight nil curb-curved nil))

                      (qhpe:safe-set-clayer qhpe:*diagram-layer-name*)
                      (qhpe:cleanup-temp)
                      (setq draw-result
                        (vl-catch-all-apply
                          'qhpe23:draw-base-diagram
                          (list insertion total layers diagram-data
                            start-station station-step band-height text-height
                            qhpe:*diagram-layer-name* curb-layer
                            curb-straight curb-curved)))
                      (if (vl-catch-all-error-p draw-result)
                        (*error*
                          (strcat "Falha ao desenhar o diagrama: "
                            (vl-catch-all-error-message draw-result)))
                        (progn
                          (vla-Regen doc 1)
                          (qhpe23:restore-environment
                            doc old-clayer old-cmdecho old-osmode old-error)
                          (princ
                            (strcat "\nDiagrama concluido: "
                              (itoa (length layers)) " pavimentos"
                              (if curb-ss " + meios-fios." ".")))
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
  )
  (princ)
)

(defun c:QHPEA
  (/ *error* old-error acad doc space old-cmdecho old-osmode old-clayer
    temp-layer axis-selection axis total hatch-ss station-step station-text
    start-station search-width linear-factor insertion text-height band-height
    counter hatch hatch-records records layers area-rows curb-layer curb-ss
    ranges curb-analysis curb-rows draw-result include-curb)

  (setq old-error *error*
        acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        old-cmdecho (getvar "CMDECHO")
        old-osmode (getvar "OSMODE")
        old-clayer (getvar "CLAYER"))

  (defun *error* (message)
    (qhpe:safe-set-clayer qhpe:*table-layer-name*)
    (qhpe:cleanup-temp)
    (qhpe23:restore-environment
      doc old-clayer old-cmdecho old-osmode old-error)
    (if
      (and message
        (not (wcmatch (strcase message) "*CANCEL*,*QUIT*,*EXIT*")))
      (princ (strcat "\nErro QHPEA: " message)))
    (princ)
  )

  (vla-StartUndoMark doc)
  (setvar "CMDECHO" 0)
  (setvar "OSMODE" 0)
  (setq temp-layer (qhpe:prepare-temp-layer doc))
  (qhpe:get-layer-object doc qhpe:*table-layer-name*)

  (princ "\nQHPEA - Tabelas nativas com areas e meios-fios")
  (while
    (progn
      (setq axis-selection
        (entsel "\nSelecione a mesma polilinha usada no diagrama: "))
      (and axis-selection
        (not (qhpe:valid-axis-polyline-p (car axis-selection))))
    )
  )
  (if (null axis-selection)
    (*error* "Polilinha nao selecionada.")
    (progn
      (setq axis (car axis-selection)
            total (qhpe:curve-length axis))
      (if (not (and (numberp total) (> total 1.0e-8)))
        (*error* "A polilinha selecionada nao possui comprimento valido.")
        (progn
          (princ "\nSelecione as mesmas hachuras usadas no diagrama:")
          (setq hatch-ss (ssget "_:L" '((0 . "HATCH"))))
          (if (null hatch-ss)
            (*error* "Nenhuma hachura selecionada.")
            (progn
              (setq curb-layer
                (qhpe23:prompt-curb-layer
                  "\nSelecione uma polilinha do layer dos meios-fios ou Enter para ignorar: "
                ))
              (setq curb-ss (qhpe23:curb-selection-set curb-layer)
                    include-curb (if curb-ss T nil))

              (setq station-step
                (qhpe:prompt-real-default
                  "\nComprimento de cada estaca nas unidades do desenho"
                  qhpe:*interval-default*))
              (setq qhpe:*interval-default* station-step)
              (setq station-text
                (getstring T "\nEstaca no inicio da polilinha <0+0.00>: "))
              (if (= station-text "") (setq station-text "0+0.00"))
              (setq start-station
                (qhpe:parse-station station-text station-step))
              (setq search-width
                (qhpe:prompt-real-default
                  "\nLargura de busca para o lado ESQUERDO da polilinha"
                  qhpe:*width-default*))
              (setq qhpe:*width-default* search-width
                    qhpe:*right-width-current*
                      (qhpe:prompt-real-default
                        "\nLargura de busca para o lado DIREITO da polilinha"
                        (if (and (numberp qhpe:*right-width-default*) (> qhpe:*right-width-default* 0.0))
                          qhpe:*right-width-default* search-width))
                    qhpe:*right-width-default* qhpe:*right-width-current*)
              (setq linear-factor
                (qhpe:prompt-real-default
                  "\nFator linear para converter a unidade do desenho em metros (1 para desenho em metros)"
                  qhpe:*linear-factor-default*))
              (setq qhpe:*linear-factor-default* linear-factor)
              (setq insertion
                (getpoint "\nClique exatamente no ponto inicial inferior do diagrama: "))
              (if (null insertion)
                (*error* "Ponto inicial do diagrama nao informado.")
                (progn
                  (if (null qhpe:*diagram-text-height-default*)
                    (setq qhpe:*diagram-text-height-default*
                      (* station-step 0.025)))
                  (setq text-height
                    (qhpe:prompt-real-default
                      "\nAltura dos textos das tabelas"
                      qhpe:*diagram-text-height-default*))
                  (setq qhpe:*diagram-text-height-default* text-height)
                  (if (null qhpe:*diagram-band-height-default*)
                    (setq qhpe:*diagram-band-height-default*
                      (* station-step 0.12)))
                  (setq band-height
                    (qhpe:prompt-real-default
                      "\nAltura das faixas usada no diagrama"
                      qhpe:*diagram-band-height-default*))
                  (setq qhpe:*diagram-band-height-default* band-height)

                  (princ "\nGerando os limites das hachuras...")
                  (setq counter 0 records nil)
                  (repeat (sslength hatch-ss)
                    (setq hatch (ssname hatch-ss counter)
                          hatch-records
                            (qhpe:make-hatch-records hatch space))
                    (if hatch-records
                      (setq records (append hatch-records records)))
                    (setq counter (1+ counter)))

                  (if (null records)
                    (*error* "Nenhum limite de hachura valido foi criado.")
                    (progn
                      (setq layers (qhpe:unique-layers records))
                      (princ "\nCalculando as areas por trecho...")
                      (setq area-rows
                        (qhpe:process-intervals
                          axis total records layers start-station station-step
                          search-width linear-factor space))
                      (setq layers
                        (qhpe:sort-layers-by-total layers area-rows))

                      (setq ranges
                        (qhpe23:station-ranges
                          total start-station station-step))
                      (if include-curb
                        (progn
                          (princ "\nCalculando meio-fio reto e curvo por trecho...")
                          (setq curb-analysis
                            (qhpe23:analyze-curbs
                              axis total curb-ss ranges station-step search-width)
                                curb-rows (nth 0 curb-analysis))
                        )
                        (setq curb-rows nil))

                      (qhpe:safe-set-clayer qhpe:*table-layer-name*)
                      (qhpe:cleanup-temp)
                      (setq draw-result
                        (vl-catch-all-apply
                          'qhpe23:draw-native-tables
                          (list insertion area-rows layers curb-rows
                            start-station station-step linear-factor
                            band-height text-height qhpe:*table-layer-name*
                            include-curb)))
                      (if (vl-catch-all-error-p draw-result)
                        (*error*
                          (strcat "Falha ao inserir as tabelas: "
                            (vl-catch-all-error-message draw-result)))
                        (progn
                          (vla-Regen doc 1)
                          (qhpe23:restore-environment
                            doc old-clayer old-cmdecho old-osmode old-error)
                          (princ
                            (strcat "\nTabelas concluidas: "
                              (itoa (length area-rows))
                              " trechos"
                              (if include-curb " com meios-fios." ".")))
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
  )
  (princ)
)

;;; ================================================================
;;;
;;; Ajustes:
;;;   - folga entre pavimentos e meios-fios;
;;;   - folga entre MEIO-FIO RETO e MEIO-FIO CURVO;
;;;   - layers de saida separados para reto e curvo;
;;;   - meio-fio curvo em magenta (ACI 6);
;;;   - descontinuidades calculadas somente pelos pavimentos;
;;;   - geometria atualizada tambem para posicionamento das tabelas;
;;;   - largura de colunas calculada com margem e fator de seguranca;
;;;   - recomputacao final das larguras depois da reducao do texto.
;;; ================================================================

(setq qhpe:*version* "2.4")
(setq qhpe24:*curb-layer-straight* "QHPE_MEIO_FIO_RETO")
(setq qhpe24:*curb-layer-curved* "QHPE_MEIO_FIO_CURVO")

(defun qhpe24:valid-layer-color (value)
  (if (and (numberp value) (>= value 1) (<= value 255)) value 7)
)

(defun qhpe24:ensure-output-layer (doc name color / layer)
  (setq layer (qhpe:get-layer-object doc name))
  (if layer
    (progn
      (vl-catch-all-apply 'vla-put-LayerOn (list layer :vlax-true))
      (vl-catch-all-apply 'vla-put-Freeze (list layer :vlax-false))
      (vl-catch-all-apply 'vla-put-Lock (list layer :vlax-false))
      (vl-catch-all-apply 'vla-put-Color (list layer color))
    )
  )
  layer
)

(defun qhpe24:curb-output-layers
  (source-layer-name / acad doc source-layer source-result source-color)
  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        source-result
          (vl-catch-all-apply
            'vla-Item
            (list (vla-get-Layers doc) source-layer-name)
          )
        source-color 7)
  (if (not (vl-catch-all-error-p source-result))
    (progn
      (setq source-layer source-result
            source-result
              (vl-catch-all-apply 'vla-get-Color (list source-layer)))
      (if (not (vl-catch-all-error-p source-result))
        (setq source-color (qhpe24:valid-layer-color source-result))
      )
    )
  )
  (qhpe24:ensure-output-layer
    doc qhpe24:*curb-layer-straight* source-color)
  (qhpe24:ensure-output-layer
    doc qhpe24:*curb-layer-curved* 6)
  (list qhpe24:*curb-layer-straight* qhpe24:*curb-layer-curved*)
)

;;;   0 band-base | 1 curb-height | 2 pavement-base | 3 band-top
;;; Acrescenta:
;;;   4 gap entre meios-fios | 5 gap entre meios-fios e pavimentos
(defun qhpe23:diagram-geometry
  (insertion station-step pavement-count band-height text-height has-curb
    / y0 band-base curb-height curb-gap curb-pavement-gap curb-total
      pavement-base band-top)
  (setq y0 (cadr insertion)
        band-base
          (+ y0 (max (* 2.35 text-height) (* 0.08 station-step)))
        curb-height
          (if has-curb (qhpe23:curb-height band-height text-height) 0.0)
        curb-gap
          (if has-curb
            (max (* 0.45 text-height) (* 0.055 band-height))
            0.0)
        curb-pavement-gap
          (if has-curb
            (max (* 0.85 text-height) (* 0.14 band-height))
            0.0)
        curb-total
          (if has-curb
            (+ (* 2.0 curb-height) curb-gap curb-pavement-gap)
            0.0)
        pavement-base (+ band-base curb-total)
        band-top (+ pavement-base (* pavement-count band-height)))
  (list
    band-base curb-height pavement-base band-top
    curb-gap curb-pavement-gap)
)

(defun qhpe23:draw-base-diagram
  (insertion total layers diagram-data start-station station-step band-height
    text-height annotation-layer curb-layer curb-straight curb-curved
    / x0 y0 layer-count has-curb geometry band-base curb-height pavement-base
      band-top curb-gap style index layer-name layer-data occurrence by0 by1
      main distance x label-y station-label discontinuities disc-index
      disc-level disc-bottom disc-text-y straight-y0 straight-y1 curved-y0
      curved-y1 output-layers straight-layer curved-layer)

  (setq x0 (car insertion)
        y0 (cadr insertion)
        layer-count (length layers)
        has-curb (if curb-layer T nil)
        geometry
          (qhpe23:diagram-geometry
            insertion station-step layer-count band-height text-height has-curb)
        band-base (nth 0 geometry)
        curb-height (nth 1 geometry)
        pavement-base (nth 2 geometry)
        band-top (nth 3 geometry)
        curb-gap (nth 4 geometry)
        style (qhpe21:ensure-romans-style)
        label-y (- y0 (* 1.50 text-height)))

  (princ "\nDesenhando as faixas dos pavimentos...")
  (setq index 0)
  (foreach layer-name layers
    (setq by0
      (+ pavement-base (* (- layer-count index 1) band-height))
    )
    (setq by1 (+ by0 band-height)
          layer-data (assoc layer-name diagram-data))
    (if layer-data
      (foreach occurrence (cdr layer-data)
        (if
          (and
            (listp occurrence)
            (numberp (car occurrence))
            (numberp (cadr occurrence))
            (> (- (cadr occurrence) (car occurrence)) 1.0e-8))
          (qhpe31:make-pavement-band
            (+ x0 (car occurrence)) (+ x0 (cadr occurrence))
            by0 by1 layer-name)
        )
      )
    )
    (qhpe21:make-text-aligned
      (list (- x0 (* 0.40 text-height)) (+ by0 (/ band-height 2.0)) 0.0)
      layer-name text-height 0.0 2 2 annotation-layer style)
    (setq index (1+ index))
  )

  (if has-curb
    (progn
      (setq output-layers (qhpe24:curb-output-layers curb-layer)
            straight-layer (car output-layers)
            curved-layer (cadr output-layers)
            curved-y0 band-base
            curved-y1 (+ curved-y0 curb-height)
            straight-y0 (+ curved-y1 curb-gap)
            straight-y1 (+ straight-y0 curb-height))

      (princ "\nDesenhando as linhas de meios-fios em layers separados...")
      (foreach occurrence curb-curved
        (if (and (listp occurrence) (numberp (car occurrence))
                 (numberp (cadr occurrence)))
          (qhpe21:make-band
            (+ x0 (car occurrence)) (+ x0 (cadr occurrence))
            curved-y0 curved-y1 curved-layer)
        )
      )
      (foreach occurrence curb-straight
        (if (and (listp occurrence) (numberp (car occurrence))
                 (numberp (cadr occurrence)))
          (qhpe21:make-band
            (+ x0 (car occurrence)) (+ x0 (cadr occurrence))
            straight-y0 straight-y1 straight-layer)
        )
      )
      (qhpe21:make-text-aligned
        (list (- x0 (* 0.40 text-height))
              (+ straight-y0 (/ curb-height 2.0)) 0.0)
        qhpe23:*curb-label-straight* (* 0.86 text-height)
        0.0 2 2 annotation-layer style)
      (qhpe21:make-text-aligned
        (list (- x0 (* 0.40 text-height))
              (+ curved-y0 (/ curb-height 2.0)) 0.0)
        qhpe23:*curb-label-curved* (* 0.86 text-height)
        0.0 2 2 annotation-layer style)
    )
  )

  (princ "\nInserindo linhas e textos das estacas principais...")
  (setq main
    (qhpe21:main-station-distances total start-station station-step))
  (foreach distance main
    (if (numberp distance)
      (progn
        (setq x (+ x0 distance)
              station-label
                (qhpe:format-station-diagram
                  (+ start-station distance) station-step))
        (qhpe21:make-line
          (list x y0 0.0) (list x band-top 0.0) annotation-layer)
        (qhpe21:make-text-aligned
          (list x label-y 0.0) station-label text-height 0.0
          1 0 annotation-layer style)
      )
    )
  )

  ;; IMPORTANTE: somente os pavimentos entram nesta lista. Os meios-fios
  ;; nao geram textos nem linhas de inicio/fim de ocorrencia.
  (princ "\nInserindo estacas de inicio e fim somente dos pavimentos...")
  (setq discontinuities
    (qhpe21:discontinuity-distances diagram-data main total)
        disc-index 0)
  (foreach distance discontinuities
    (if (numberp distance)
      (progn
        (setq x (+ x0 distance)
              disc-level (rem disc-index 2)
              station-label
                (qhpe:format-station-diagram
                  (+ start-station distance) station-step)
              disc-bottom (- y0 (* 8.40 text-height))
              disc-text-y
                (- y0 (* (+ 6.75 (* disc-level 1.00)) text-height)))
        (qhpe31:make-discontinuity-line
          (list x disc-bottom 0.0) (list x band-top 0.0) annotation-layer)
        (qhpe21:make-text-left
          (list (- x (* 0.24 text-height)) disc-text-y 0.0)
          station-label text-height (/ pi 2.0) annotation-layer style)
        (setq disc-index (1+ disc-index))
      )
    )
  )
  T
)

(defun qhpe24:visible-text-length (text / position size count)
  (setq position 1 size (strlen text) count 0)
  (while (<= position size)
    (if
      (and
        (<= (+ position 7) size)
        (= (substr text position 3) "\\U+")
      )
      (setq position (+ position 8)
            count (1+ count))
      (setq position (1+ position)
            count (1+ count))
    )
  )
  count
)

(defun qhpe24:safe-text-width (text height style / measured estimated)
  (setq measured (qhpe22:text-width text height style)
        estimated
          (* (float (qhpe24:visible-text-length text)) height 0.72))
  ;; O limite por caracteres evita que fontes SHX e textos dentro de TABLE
  ;; sejam subestimados pelo bounding box de um TEXT temporario.
  (max estimated (* 1.16 measured))
)

(defun qhpe24:max-safe-text-width
  (texts height style / maximum text width)
  (setq maximum 0.0)
  (foreach text texts
    (setq width (qhpe24:safe-text-width text height style))
    (if (> width maximum) (setq maximum width))
  )
  maximum
)

(defun qhpe23:table-widths
  (rows height style / service-texts quantity-texts accum-texts row
    margin-x w1 w2 w3)
  (setq service-texts (list "SERVI\\U+00C7O")
        quantity-texts (list "QUANTIDADE")
        accum-texts (list "ACUMULADO"))
  (foreach row rows
    (setq service-texts (cons (nth 0 row) service-texts)
          quantity-texts (cons (nth 1 row) quantity-texts)
          accum-texts (cons (nth 2 row) accum-texts))
  )
  (setq margin-x (* 0.55 height)
        w1 (+ (qhpe24:max-safe-text-width service-texts height style)
              (* 2.0 margin-x))
        w2 (+ (qhpe24:max-safe-text-width quantity-texts height style)
              (* 2.0 margin-x))
        w3 (+ (qhpe24:max-safe-text-width accum-texts height style)
              (* 2.0 margin-x)))
  (list w1 w2 w3)
)

(defun qhpe24:widths-with-title
  (title rows height style / widths w1 w2 w3 total title-width margin-x extra)
  (setq widths (qhpe23:table-widths rows height style)
        w1 (nth 0 widths)
        w2 (nth 1 widths)
        w3 (nth 2 widths)
        total (+ w1 w2 w3)
        margin-x (* 0.55 height)
        title-width
          (+ (qhpe24:safe-text-width title height style)
             (* 2.0 margin-x)))
  (if (> title-width total)
    (progn
      (setq extra (/ (- title-width total) 3.0)
            w1 (+ w1 extra)
            w2 (+ w2 extra)
            w3 (+ w3 extra))
    )
  )
  (list w1 w2 w3)
)

(defun qhpe23:fit-table
  (title rows preferred-height available-width style
    / height minimum-height target-width iteration done widths total scale)
  (setq height preferred-height
        minimum-height (* preferred-height 0.18)
        target-width (* 0.965 available-width)
        iteration 0
        done nil)
  (while (and (< iteration 10) (not done))
    (setq widths (qhpe24:widths-with-title title rows height style)
          total (+ (nth 0 widths) (nth 1 widths) (nth 2 widths)))
    (if (or (<= total target-width) (<= height (+ minimum-height 1.0e-9)))
      (setq done T)
      (progn
        (setq scale (/ target-width total)
              height
                (max minimum-height (* height (max 0.45 (* 0.97 scale)))))
      )
    )
    (setq iteration (1+ iteration))
  )
  ;; larguras calculadas antes da ultima reducao de altura.
  (setq widths (qhpe24:widths-with-title title rows height style))
  (list height (nth 0 widths) (nth 1 widths) (nth 2 widths))
)

(defun qhpe23:create-native-table
  (mid-x available-width bottom title rows preferred-height layer style
    / acad doc space fit height w1 w2 w3 total-width margin-x margin-y
      title-height row-height row-count table-height top-left table row-index
      data-row row)

  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        fit (qhpe23:fit-table title rows preferred-height available-width style)
        height (nth 0 fit)
        w1 (nth 1 fit)
        w2 (nth 2 fit)
        w3 (nth 3 fit)
        total-width (+ w1 w2 w3)
        margin-x (* 0.55 height)
        margin-y (* 0.30 height)
        title-height (+ (* 1.28 height) (* 2.0 margin-y))
        row-height (+ (* 1.16 height) (* 2.0 margin-y))
        row-count (+ 2 (length rows))
        table-height (+ title-height (* (1- row-count) row-height))
        top-left
          (list (- mid-x (/ total-width 2.0)) (+ bottom table-height) 0.0))

  (setq table
    (vla-AddTable
      space (vlax-3d-point top-left) row-count 3 row-height w1))
  (if table
    (progn
      (qhpe23:safe-table-call 'vla-put-Layer (list table layer))
      (qhpe27:apply-table-style table)
      (qhpe23:safe-table-call
        'vla-put-RegenerateTableSuppressed (list table :vlax-true))
      (qhpe23:safe-table-call 'vla-put-HorzCellMargin (list table margin-x))
      (qhpe23:safe-table-call 'vla-put-VertCellMargin (list table margin-y))
      (qhpe23:safe-table-call 'vla-put-TitleSuppressed (list table :vlax-false))
      (qhpe23:safe-table-call 'vla-put-HeaderSuppressed (list table :vlax-false))

      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 0 w1))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 1 w2))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 2 w3))
      (qhpe23:safe-table-call 'vla-SetRowHeight (list table 0 title-height))
      (setq row-index 1)
      (while (< row-index row-count)
        (qhpe23:safe-table-call
          'vla-SetRowHeight (list table row-index row-height))
        (setq row-index (1+ row-index))
      )

      (qhpe23:safe-table-call 'vla-MergeCells (list table 0 0 0 2))
      (qhpe23:safe-table-call
        'vla-SetTextStyle
        (list table (+ qhpe:acTitleRow qhpe:acHeaderRow qhpe:acDataRow) style))
      (qhpe23:safe-table-call
        'vla-SetTextHeight
        (list table (+ qhpe:acTitleRow qhpe:acHeaderRow qhpe:acDataRow) height))

      (qhpe:set-table-text table 0 0 title)
      (qhpe:set-table-text table 1 0 "SERVI\\U+00C7O")
      (qhpe:set-table-text table 1 1 "QUANTIDADE")
      (qhpe:set-table-text table 1 2 "ACUMULADO")
      (qhpe23:safe-table-call
        'vla-SetCellAlignment (list table 0 0 qhpe23:acMiddleCenter))
      (setq row-index 0)
      (while (< row-index 3)
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table 1 row-index qhpe23:acMiddleCenter))
        (setq row-index (1+ row-index))
      )

      (setq data-row 2)
      (foreach row rows
        (qhpe:set-table-text table data-row 0 (nth 0 row))
        (qhpe:set-table-text table data-row 1 (nth 1 row))
        (qhpe:set-table-text table data-row 2 (nth 2 row))
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table data-row 0 qhpe23:acMiddleLeft))
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table data-row 1 qhpe23:acMiddleRight))
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table data-row 2 qhpe23:acMiddleRight))
        (setq data-row (1+ data-row))
      )

      ;; Reaplica as larguras depois de preencher as celulas. Algumas versoes
      ;; recalculam a largura durante o preenchimento quando a regeneracao esta
      ;; suprimida.
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 0 w1))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 1 w2))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 2 w3))
      (qhpe23:safe-table-call
        'vla-put-RegenerateTableSuppressed (list table :vlax-false))
      (qhpe23:safe-table-call 'vla-RecomputeTableBlock (list table :vlax-true))
      (qhpe23:safe-table-call 'vla-Update (list table))
    )
  )
  table
)

;;; ================================================================
;;;
;;; Comandos gerenciados:
;;;   QHPEGERAR  - gera diagrama, tabelas por estaca e legenda/totais;
;;;   QHPEUPDATE - atualiza um conjunto gerado pelo QHPE;
;;;
;;; Recursos:
;;;   - configuracoes persistentes via SETENV/GETENV;
;;;   - verificacao previa dos dados;
;;;   - grupo AutoCAD com metadados para atualizacao;
;;;   - legenda generica + tabela geral de totais;
;;;   - exportacao direta e formatada para Excel (3 planilhas).
;;; ================================================================

(setq qhpe:*version* "2.5")
(setq qhpe25:*group-prefix* "QHPE25_")
(setq qhpe25:*last-generated-group* nil)
(setq qhpe25:*env-prefix* "QHPE25_")

(defun qhpe25:str-empty-p (value)
  (or (null value) (= value ""))
)

(defun qhpe25:env-name (name)
  (strcat qhpe25:*env-prefix* name)
)

(defun qhpe25:getenv-string (name default / value)
  (setq value (getenv (qhpe25:env-name name)))
  (if (qhpe25:str-empty-p value) default value)
)

(defun qhpe25:getenv-real (name default / value number)
  (setq value (getenv (qhpe25:env-name name)))
  (if (qhpe25:str-empty-p value)
    default
    (progn
      (setq number (distof value 2))
      (if (numberp number) number default)
    )
  )
)

(defun qhpe25:setenv-string (name value)
  (setenv (qhpe25:env-name name) (if value value ""))
)

(defun qhpe25:setenv-real (name value)
  (if (numberp value)
    (setenv (qhpe25:env-name name) (rtos value 2 12))
  )
)

(defun qhpe25:save-settings
  (station-step start-station search-width linear-factor text-height band-height
    curb-layer export-choice)
  (qhpe25:setenv-real "STEP" station-step)
  (qhpe25:setenv-real "START" start-station)
  (qhpe25:setenv-real "WIDTH" search-width)
  (qhpe25:setenv-real "WIDTH_RIGHT"
    (if (and (numberp qhpe:*right-width-current*) (> qhpe:*right-width-current* 0.0))
      qhpe:*right-width-current*
      search-width))
  (qhpe25:setenv-real "FACTOR" linear-factor)
  (qhpe25:setenv-real "TEXT" text-height)
  (qhpe25:setenv-real "BAND" band-height)
  (qhpe25:setenv-string "CURB_LAYER" (if curb-layer curb-layer ""))
  (qhpe25:setenv-string "EXPORT" (if export-choice "SIM" "NAO"))
  T
)

(defun qhpe25:prompt-positive-real (message default / value)
  (setq value (qhpe:prompt-real-default message default))
  (while (or (not (numberp value)) (<= value 0.0))
    (princ "\nInforme um valor numerico maior que zero.")
    (setq value (qhpe:prompt-real-default message default))
  )
  value
)

(defun qhpe25:prompt-yes-no (message default-yes / answer suffix)
  (setq suffix (if default-yes " <Sim>: " " <Nao>: "))
  (initget "Sim Nao")
  (setq answer (getkword (strcat message " [Sim/Nao]" suffix)))
  (if (null answer) default-yes (= answer "Sim"))
)

(defun qhpe25:handle-of (ent)
  (if ent (cdr (assoc 5 (entget ent))))
)

(defun qhpe25:ss-handles (ss / index result ent handle)
  (setq index 0 result nil)
  (if ss
    (repeat (sslength ss)
      (setq ent (ssname ss index)
            handle (qhpe25:handle-of ent))
      (if handle (setq result (cons handle result)))
      (setq index (1+ index))
    )
  )
  (reverse result)
)

(defun qhpe25:ss-from-handles (handles entity-type / ss ent)
  (setq ss (ssadd))
  (foreach handle handles
    (setq ent (handent handle))
    (if
      (and ent
        (or (null entity-type)
          (= (strcase (cdr (assoc 0 (entget ent)))) (strcase entity-type))))
      (ssadd ent ss)
    )
  )
  (if (> (sslength ss) 0) ss nil)
)

(defun qhpe25:layer-state-warning (doc layer-name / layer result warnings)
  (setq result
    (vl-catch-all-apply 'vla-Item (list (vla-get-Layers doc) layer-name)))
  (if (vl-catch-all-error-p result)
    (list (strcat "Layer inexistente: " layer-name))
    (progn
      (setq layer result warnings nil)
      (if (= (vla-get-LayerOn layer) :vlax-false)
        (setq warnings (cons (strcat "Layer desligado: " layer-name) warnings)))
      (if (= (vla-get-Freeze layer) :vlax-true)
        (setq warnings (cons (strcat "Layer congelado: " layer-name) warnings)))
      (if (= (vla-get-Lock layer) :vlax-true)
        (setq warnings (cons (strcat "Layer bloqueado: " layer-name) warnings)))
      warnings
    )
  )
)

(defun qhpe25:append-unique-warning (warning warnings)
  (if (and warning (not (member warning warnings)))
    (cons warning warnings)
    warnings)
)

(defun qhpe25:preflight
  (doc axis hatch-ss curb-layer station-step search-width linear-factor
    text-height band-height / warnings total index ent obj result area normal
    elevation layer hatch-layers curb-ss curb-count)

  (setq warnings nil
        total (qhpe:curve-length axis))
  (if (not (and (numberp total) (> total 1.0e-8)))
    (setq warnings (cons "O eixo nao possui comprimento valido." warnings)))

  (setq elevation (cdr (assoc 38 (entget axis))))
  (if (and (numberp elevation) (> (abs elevation) 1.0e-8))
    (setq warnings
      (cons "O eixo possui elevacao diferente de zero; confirme a coplanaridade."
        warnings)))

  (if (or (not (numberp station-step)) (<= station-step 0.0))
    (setq warnings (cons "Comprimento de estaca invalido." warnings)))
  (if (or (not (numberp search-width)) (<= search-width 0.0))
    (setq warnings (cons "Largura de busca do lado ESQUERDO invalida." warnings)))
  (if (or (not (numberp qhpe:*right-width-current*))
          (<= qhpe:*right-width-current* 0.0))
    (setq warnings (cons "Largura de busca do lado DIREITO invalida." warnings)))
  (if (or (not (numberp linear-factor)) (<= linear-factor 0.0))
    (setq warnings (cons "Fator linear invalido." warnings)))
  (if (or (not (numberp text-height)) (<= text-height 0.0))
    (setq warnings (cons "Altura de texto invalida." warnings)))
  (if (or (not (numberp band-height)) (<= band-height 0.0))
    (setq warnings (cons "Altura das faixas invalida." warnings)))
  (if (and (numberp station-step) (or (< station-step 0.10) (> station-step 1000.0)))
    (setq warnings
      (cons "O comprimento da estaca parece incomum para as unidades do desenho."
        warnings)))

  (setq index 0 hatch-layers nil)
  (if (null hatch-ss)
    (setq warnings (cons "Nenhuma hachura foi fornecida." warnings))
    (repeat (sslength hatch-ss)
      (setq ent (ssname hatch-ss index)
            obj (qhpe:safe-vla-object ent)
            layer (cdr (assoc 8 (entget ent)))
            hatch-layers (if (member layer hatch-layers)
                           hatch-layers (cons layer hatch-layers)))
      (if obj
        (progn
          (setq result (vl-catch-all-apply 'vla-get-Area (list obj)))
          (if (vl-catch-all-error-p result)
            (setq warnings
              (cons
                (strcat "Area nativa da HATCH indisponivel no layer " layer
                  " (Handle " (cdr (assoc 5 (entget ent)))
                  "). O QHPE tentara quantificar pelos contornos gerados; use QHPEDIAGHATCH para diagnosticar.")
                warnings))
            (if (<= result 1.0e-10)
              (setq warnings
                (cons
                  (strcat "Hachura com Area=0 no layer " layer
                    " (Handle " (cdr (assoc 5 (entget ent)))
                    "). Use QHPEDIAGHATCH para diagnosticar.")
                  warnings))))
        )
        (setq warnings (cons "Nao foi possivel acessar uma das hachuras." warnings)))
      (setq normal (cdr (assoc 210 (entget ent))))
      (if
        (and normal
          (or (> (abs (car normal)) 1.0e-8)
              (> (abs (cadr normal)) 1.0e-8)
              (> (abs (- (caddr normal) 1.0)) 1.0e-8)))
        (setq warnings
          (cons (strcat "Hachura fora do plano XY no layer " layer ".") warnings)))
      (setq index (1+ index))
    )
  )

  (foreach layer hatch-layers
    (foreach warning (qhpe25:layer-state-warning doc layer)
      (setq warnings (qhpe25:append-unique-warning warning warnings))))

  (setq curb-ss (qhpe23:curb-selection-set curb-layer)
        curb-count (if curb-ss (sslength curb-ss) 0))
  (if (and curb-layer (= curb-count 0))
    (setq warnings
      (cons (strcat "Nenhum objeto de meio-fio encontrado no layer " curb-layer ".")
        warnings)))

  (princ "\n--- VERIFICACAO PREVIA QHPE ---")
  (princ (strcat "\nEixo: " (rtos total 2 3) " unidades"))
  (princ (strcat "\nHachuras: " (if hatch-ss (itoa (sslength hatch-ss)) "0")))
  (princ (strcat "\nLayers de pavimento: " (itoa (length hatch-layers))))
  (princ (strcat "\nMeios-fios encontrados: " (itoa curb-count)))
  (princ
    (strcat "\nEstaca: " (rtos station-step 2 3)
      " | Busca: " (rtos search-width 2 3)
      " | Fator: " (rtos linear-factor 2 6)))

  (if warnings
    (progn
      (princ (strcat "\nAvisos encontrados: " (itoa (length warnings))))
      (foreach warning (reverse warnings)
        (princ (strcat "\n  - " warning)))
      (qhpe25:prompt-yes-no "\nContinuar mesmo assim?" nil)
    )
    (progn
      (princ "\nNenhum problema impeditivo foi identificado.")
      T
    )
  )
)

(defun qhpe25:prompt-curb-layer-saved (/ saved choice selected)
  (setq saved (qhpe25:getenv-string "CURB_LAYER" ""))
  (if (qhpe25:str-empty-p saved)
    (qhpe23:prompt-curb-layer
      "\nSelecione uma polilinha do layer dos meios-fios ou Enter para ignorar: ")
    (progn
      (initget "Ultimo Selecionar Ignorar")
      (setq choice
        (getkword
          (strcat "\nMeios-fios [Ultimo/Selecionar/Ignorar] <Ultimo: " saved ">: ")))
      (cond
        ((or (null choice) (= choice "Ultimo")) saved)
        ((= choice "Selecionar")
          (qhpe23:prompt-curb-layer
            "\nSelecione uma polilinha do layer dos meios-fios: "))
        (T nil)
      )
    )
  )
)

(defun qhpe25:compute-data
  (axis hatch-ss curb-layer station-step start-station search-width linear-factor
    space / total counter hatch hatch-records records layers projected-result
    projected diagram-data area-rows ranges curb-ss curb-analysis curb-rows
    curb-straight curb-curved curb-total-straight curb-total-curved)

  (setq total (qhpe:curve-length axis)
        counter 0
        records nil)
  (princ "\nGerando limites e verificando ilhas das hachuras...")
  (repeat (sslength hatch-ss)
    (setq hatch (ssname hatch-ss counter)
          hatch-records (qhpe:make-hatch-records hatch space))
    (if hatch-records (setq records (append hatch-records records)))
    (setq counter (1+ counter))
  )
  (if (null records)
    nil
    (progn
      (setq layers (qhpe:unique-layers records))
      (princ "\nCalculando areas por estaca...")
      (setq area-rows
        (qhpe:process-intervals axis total records layers start-station
          station-step search-width linear-factor space))
      (setq layers (qhpe:sort-layers-by-total layers area-rows))

      (princ "\nCalculando inicios e finais dos pavimentos...")
      (setq projected-result
        (vl-catch-all-apply 'qhpe:projection-intervals-by-layer
          (list axis total records station-step)))
      (if (vl-catch-all-error-p projected-result)
        (setq projected nil)
        (setq projected projected-result))
      (setq diagram-data (qhpe2:merged-projections layers projected))

      (setq ranges (qhpe23:station-ranges total start-station station-step)
            curb-ss (qhpe23:curb-selection-set curb-layer))
      (if curb-ss
        (progn
          (princ "\nCalculando meios-fios retos e curvos...")
          (setq curb-analysis
            (qhpe23:analyze-curbs axis total curb-ss ranges station-step)
                curb-rows (nth 0 curb-analysis)
                curb-straight (nth 1 curb-analysis)
                curb-curved (nth 2 curb-analysis)
                curb-total-straight (nth 3 curb-analysis)
                curb-total-curved (nth 4 curb-analysis)))
        (setq curb-rows nil curb-straight nil curb-curved nil
              curb-total-straight 0.0 curb-total-curved 0.0))

      (list
        (cons 'TOTAL total)
        (cons 'LAYERS layers)
        (cons 'DIAGRAM-DATA diagram-data)
        (cons 'AREA-ROWS area-rows)
        (cons 'CURB-ROWS curb-rows)
        (cons 'CURB-STRAIGHT curb-straight)
        (cons 'CURB-CURVED curb-curved)
        (cons 'CURB-TOTAL-STRAIGHT curb-total-straight)
        (cons 'CURB-TOTAL-CURVED curb-total-curved)
        (cons 'HAS-CURB (if curb-ss T nil))
      )
    )
  )
)

(defun qhpe25:data-get (key data)
  (cdr (assoc key data))
)

(defun qhpe25:layer-total-area (area-rows layer / total row)
  (setq total 0.0)
  (foreach row area-rows
    (setq total (+ total (max 0.0 (qhpe:row-area (nth 3 row) layer)))))
  total
)

(defun qhpe25:summary-rows
  (data linear-factor / rows layers area-rows layer total has-curb)
  (setq rows nil
        layers (qhpe25:data-get 'LAYERS data)
        area-rows (qhpe25:data-get 'AREA-ROWS data)
        has-curb (qhpe25:data-get 'HAS-CURB data))
  (foreach layer layers
    (setq total
      (* (qhpe25:layer-total-area area-rows layer)
        linear-factor linear-factor))
    (setq rows
      (append rows
        (list
          (list layer "m\\U+00B2" (qhpe:format-number total 2) layer total "AREA"))))
  )
  (if has-curb
    (setq rows
      (append rows
        (list
          (list qhpe23:*curb-label-straight* "m"
            (qhpe:format-number
              (* (qhpe25:data-get 'CURB-TOTAL-STRAIGHT data) linear-factor) 2)
            qhpe24:*curb-layer-straight*
            (* (qhpe25:data-get 'CURB-TOTAL-STRAIGHT data) linear-factor)
            "LINEAR")
          (list qhpe23:*curb-label-curved* "m"
            (qhpe:format-number
              (* (qhpe25:data-get 'CURB-TOTAL-CURVED data) linear-factor) 2)
            qhpe24:*curb-layer-curved*
            (* (qhpe25:data-get 'CURB-TOTAL-CURVED data) linear-factor)
            "LINEAR")))))
  rows
)

(defun qhpe25:create-summary-table
  (bottom-left rows preferred-height annotation-layer / acad doc space style
    title table-rows service-texts unit-texts total-texts row height margin-x
    margin-y w1 w2 w3 total-width title-height row-height row-count table-height
    top-left table row-index data-row swatch-x0 swatch-x1 swatch-y0 swatch-y1
    swatch-center layer-name)

  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        style (qhpe21:ensure-romans-style)
        title "LEGENDA E TOTAIS"
        table-rows rows
        height preferred-height
        service-texts (list "SERVI\\U+00C7O")
        unit-texts (list "UNIDADE")
        total-texts (list "TOTAL"))
  (foreach row rows
    (setq service-texts (cons (nth 0 row) service-texts)
          unit-texts (cons (nth 1 row) unit-texts)
          total-texts (cons (nth 2 row) total-texts)))
  (setq margin-x (* 0.62 height)
        margin-y (* 0.32 height)
        w1 (+ (qhpe24:max-safe-text-width service-texts height style)
              (* 2.0 margin-x))
        w2 (+ (qhpe24:max-safe-text-width unit-texts height style)
              (* 2.0 margin-x))
        w3 (+ (qhpe24:max-safe-text-width total-texts height style)
              (* 2.0 margin-x))
        total-width (+ w1 w2 w3)
        title-height (+ (* 1.30 height) (* 2.0 margin-y))
        row-height (+ (* 1.18 height) (* 2.0 margin-y))
        row-count (+ 2 (length rows))
        table-height (+ title-height (* (1- row-count) row-height))
        top-left
          (list (car bottom-left) (+ (cadr bottom-left) table-height) 0.0))

  (setq table
    (vla-AddTable space (vlax-3d-point top-left) row-count 3 row-height w1))
  (if table
    (progn
      (qhpe23:safe-table-call 'vla-put-Layer (list table annotation-layer))
      (qhpe27:apply-table-style table)
      (qhpe23:safe-table-call 'vla-put-RegenerateTableSuppressed
        (list table :vlax-true))
      (qhpe23:safe-table-call 'vla-put-HorzCellMargin (list table margin-x))
      (qhpe23:safe-table-call 'vla-put-VertCellMargin (list table margin-y))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 0 w1))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 1 w2))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 2 w3))
      (qhpe23:safe-table-call 'vla-SetRowHeight (list table 0 title-height))
      (setq row-index 1)
      (while (< row-index row-count)
        (qhpe23:safe-table-call 'vla-SetRowHeight
          (list table row-index row-height))
        (setq row-index (1+ row-index)))
      (qhpe23:safe-table-call 'vla-MergeCells (list table 0 0 0 2))
      (qhpe23:safe-table-call 'vla-SetTextStyle
        (list table (+ qhpe:acTitleRow qhpe:acHeaderRow qhpe:acDataRow) style))
      (qhpe23:safe-table-call 'vla-SetTextHeight
        (list table (+ qhpe:acTitleRow qhpe:acHeaderRow qhpe:acDataRow) height))
      (qhpe:set-table-text table 0 0 title)
      (qhpe:set-table-text table 1 0 "SERVI\\U+00C7O")
      (qhpe:set-table-text table 1 1 "UNIDADE")
      (qhpe:set-table-text table 1 2 "TOTAL")
      (qhpe23:safe-table-call 'vla-SetCellAlignment
        (list table 0 0 qhpe23:acMiddleCenter))
      (setq row-index 0)
      (while (< row-index 3)
        (qhpe23:safe-table-call 'vla-SetCellAlignment
          (list table 1 row-index qhpe23:acMiddleCenter))
        (setq row-index (1+ row-index)))
      (setq data-row 2)
      (foreach row rows
        (qhpe:set-table-text table data-row 0 (nth 0 row))
        (qhpe:set-table-text table data-row 1 (nth 1 row))
        (qhpe:set-table-text table data-row 2 (nth 2 row))
        (qhpe23:safe-table-call 'vla-SetCellAlignment
          (list table data-row 0 qhpe23:acMiddleLeft))
        (qhpe23:safe-table-call 'vla-SetCellAlignment
          (list table data-row 1 qhpe23:acMiddleCenter))
        (qhpe23:safe-table-call 'vla-SetCellAlignment
          (list table data-row 2 qhpe23:acMiddleRight))
        (setq data-row (1+ data-row)))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 0 w1))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 1 w2))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 2 w3))
      (qhpe23:safe-table-call 'vla-put-RegenerateTableSuppressed
        (list table :vlax-false))
      (qhpe23:safe-table-call 'vla-RecomputeTableBlock (list table :vlax-true))
      (qhpe23:safe-table-call 'vla-Update (list table))

      ;; Amostras de cor externas, alinhadas com cada linha de servico.
      (setq swatch-x1 (- (car top-left) (* 0.30 height))
            swatch-x0 (- swatch-x1 (* 1.15 height))
            data-row 0)
      (foreach row rows
        (setq swatch-center
          (- (cadr top-left) title-height row-height
            (* (+ data-row 0.5) row-height))
              swatch-y0 (- swatch-center (* 0.31 height))
              swatch-y1 (+ swatch-center (* 0.31 height))
              layer-name (nth 3 row))
        (if (and layer-name (tblsearch "LAYER" layer-name))
          (qhpe21:make-band swatch-x0 swatch-x1 swatch-y0 swatch-y1 layer-name))
        (setq data-row (1+ data-row)))
    )
  )
  (list table total-width table-height bottom-left)
)

(defun qhpe25:automatic-summary-point
  (insertion total station-step layers band-height text-height has-curb / geometry)
  (setq geometry
    (qhpe23:diagram-geometry insertion station-step (length layers)
      band-height text-height has-curb))
  (list
    (+ (car insertion) total (* 0.55 station-step))
    (nth 3 geometry)
    0.0)
)

(defun qhpe25:modelspace-new-objects (space old-count / count index result obj)
  (setq count (vla-get-Count space)
        index old-count
        result nil)
  (while (< index count)
    (setq obj (vl-catch-all-apply 'vla-Item (list space index)))
    (if (not (vl-catch-all-error-p obj))
      (setq result (cons obj result)))
    (setq index (1+ index)))
  (reverse result)
)

(defun qhpe25:group-name (/ raw)
  (setq raw (rtos (getvar "CDATE") 2 8)
        raw (qhpe:string-replace-all "_" "." raw))
  (strcat qhpe25:*group-prefix* raw)
)

(defun qhpe25:metadata-string
  (axis hatch-ss curb-layer station-step start-station search-width linear-factor
    insertion text-height band-height summary-point / metadata)
  (setq metadata
    (list
      (cons 'V "2.5")
      (cons 'A (qhpe25:handle-of axis))
      (cons 'H (qhpe25:ss-handles hatch-ss))
      (cons 'C (if curb-layer curb-layer ""))
      (cons 'S station-step)
      (cons 'ST start-station)
      (cons 'W search-width)
      (cons 'WR
        (if (and (numberp qhpe:*right-width-current*) (> qhpe:*right-width-current* 0.0))
          qhpe:*right-width-current*
          search-width))
      (cons 'F linear-factor)
      (cons 'I insertion)
      (cons 'TH text-height)
      (cons 'BH band-height)
      (cons 'L summary-point)))
  (vl-prin1-to-string metadata)
)

(defun qhpe25:create-group (doc name objects description / groups old group arr result)
  (setq groups (vla-get-Groups doc)
        old (vl-catch-all-apply 'vla-Item (list groups name)))
  (if (not (vl-catch-all-error-p old))
    (vl-catch-all-apply 'vla-Delete (list old)))
  (setq group (vl-catch-all-apply 'vla-Add (list groups name)))
  (if (vl-catch-all-error-p group)
    nil
    (progn
      (vl-catch-all-apply 'vla-put-Description (list group description))
      (if objects
        (progn
          (setq arr (qhpe:safearray-objects objects)
                result (vl-catch-all-apply 'vla-AppendItems
                  (list group (vlax-make-variant arr))))
        )
      )
      group
    )
  )
)

(defun qhpe25:group-contains-handle-p (group handle / count index obj found result)
  (setq count (vla-get-Count group)
        index 0
        found nil)
  (while (and (< index count) (not found))
    (setq result (vl-catch-all-apply 'vla-Item (list group index)))
    (if (not (vl-catch-all-error-p result))
      (progn
        (setq obj result)
        (if (= (strcase (vla-get-Handle obj)) (strcase handle))
          (setq found T))))
    (setq index (1+ index)))
  found
)

(defun qhpe25:find-group-for-entity (doc ent / groups handle found name)
  (setq groups (vla-get-Groups doc)
        handle (qhpe25:handle-of ent)
        found nil)
  (if handle
    (vlax-for group groups
      (setq name (vla-get-Name group))
      (if
        (and (not found)
          (wcmatch (strcase name) (strcat (strcase qhpe25:*group-prefix*) "*"))
          (qhpe25:group-contains-handle-p group handle))
        (setq found group))))
  found
)

(defun qhpe25:group-metadata (group / description result)
  (setq description (vl-catch-all-apply 'vla-get-Description (list group)))
  (if (or (vl-catch-all-error-p description) (qhpe25:str-empty-p description))
    nil
    (progn
      (setq result (vl-catch-all-apply 'read (list description)))
      (if (vl-catch-all-error-p result) nil result)))
)

(defun qhpe25:delete-group-and-objects (group / count index objects result)
  (setq count (vla-get-Count group)
        index 0
        objects nil)
  (while (< index count)
    (setq result (vl-catch-all-apply 'vla-Item (list group index)))
    (if (not (vl-catch-all-error-p result))
      (setq objects (cons result objects)))
    (setq index (1+ index)))
  (foreach obj objects
    (vl-catch-all-apply 'vla-Delete (list obj)))
  (vl-catch-all-apply 'vla-Delete (list group))
  T
)

(defun qhpe25:column-letter (number / result remainder)
  (setq result "")
  (while (> number 0)
    (setq remainder (rem (1- number) 26)
          result (strcat (chr (+ 65 remainder)) result)
          number (fix (/ (1- number) 26))))
  result
)

(defun qhpe25:excel-set-cell (cells row column value)
  (vl-catch-all-apply 'vlax-put-property
    (list cells 'Item row column (vlax-make-variant value)))
)

(defun qhpe25:excel-range (sheet address)
  (vlax-get-property sheet 'Range address)
)

(defun qhpe25:excel-set-borders (range / borders counter border)
  (setq borders (vlax-get-property range 'Borders)
        counter 0)
  (vlax-for border borders
    (setq counter (1+ counter))
    (if (< counter 5)
      (progn
        (vlax-put-property border 'LineStyle (vlax-make-variant 1 3))
        (vlax-put-property border 'Weight (vlax-make-variant 2 3)))
      (vlax-put-property border 'LineStyle (vlax-make-variant -4142 3))))
  (vlax-release-object borders)
)

(defun qhpe25:excel-style-sheet
  (sheet row-count col-count title / last-column title-range header-range used
    font interior columns)
  (setq last-column (qhpe25:column-letter col-count)
        title-range (qhpe25:excel-range sheet (strcat "A1:" last-column "1"))
        header-range (qhpe25:excel-range sheet (strcat "A2:" last-column "2"))
        used (qhpe25:excel-range sheet
               (strcat "A1:" last-column (itoa row-count))))
  (vlax-put-property title-range 'MergeCells :vlax-true)
  (vlax-put-property title-range 'HorizontalAlignment -4108)
  (setq font (vlax-get-property title-range 'Font))
  (vlax-put-property font 'Bold :vlax-true)
  (vlax-put-property font 'Size 12)
  (vlax-release-object font)
  (vlax-put-property header-range 'HorizontalAlignment -4108)
  (setq font (vlax-get-property header-range 'Font))
  (vlax-put-property font 'Bold :vlax-true)
  (vlax-release-object font)
  (setq interior (vlax-get-property header-range 'Interior))
  (vlax-put-property interior 'ColorIndex 15)
  (vlax-release-object interior)
  (vlax-put-property used 'VerticalAlignment -4108)
  (qhpe25:excel-set-borders used)
  (setq columns (vlax-get-property sheet 'Columns))
  (vlax-invoke-method columns 'AutoFit)
  (vlax-release-object columns)
  (vlax-release-object title-range)
  (vlax-release-object header-range)
  (vlax-release-object used)
)

(defun qhpe25:excel-fill-station-sheet
  (sheet title data start-station station-step linear-factor accumulated-p
    / cells layers area-rows curb-rows has-curb headers col row-index data-row
    layer value accum-alist curb-acc-straight curb-acc-curved curb-row final-row
    last-column number-range)
  (setq cells (vlax-get-property sheet 'Cells)
        layers (qhpe25:data-get 'LAYERS data)
        area-rows (qhpe25:data-get 'AREA-ROWS data)
        curb-rows (qhpe25:data-get 'CURB-ROWS data)
        has-curb (qhpe25:data-get 'HAS-CURB data)
        headers (append (list "ESTACA INICIAL" "ESTACA FINAL") layers
                  (if has-curb
                    (list qhpe23:*curb-label-straight* qhpe23:*curb-label-curved*)
                    nil)))
  (qhpe25:excel-set-cell cells 1 1 title)
  (setq col 1)
  (foreach header headers
    (qhpe25:excel-set-cell cells 2 col
      (cond
        ((member header layers) (strcat header " (m\U+00B2)"))
        ((= header qhpe23:*curb-label-straight*) (strcat header " (m)"))
        ((= header qhpe23:*curb-label-curved*) (strcat header " (m)"))
        (T header)))
    (setq col (1+ col)))

  (setq row-index 3 accum-alist nil curb-acc-straight 0.0 curb-acc-curved 0.0)
  (foreach data-row area-rows
    (qhpe25:excel-set-cell cells row-index 1
      (qhpe:format-station-diagram (+ start-station (nth 4 data-row)) station-step))
    (qhpe25:excel-set-cell cells row-index 2
      (qhpe:format-station-diagram (+ start-station (nth 5 data-row)) station-step))
    (setq col 3)
    (foreach layer layers
      (setq value (max 0.0 (qhpe:row-area (nth 3 data-row) layer))
            accum-alist (qhpe:update-accumulated accum-alist layer value))
      (qhpe25:excel-set-cell cells row-index col
        (* (if accumulated-p (cdr (assoc layer accum-alist)) value)
          linear-factor linear-factor))
      (setq col (1+ col)))
    (if has-curb
      (progn
        (setq curb-row (nth (- row-index 3) curb-rows)
              value (qhpe23:curb-row-value curb-row 'RETO)
              curb-acc-straight (+ curb-acc-straight value))
        (qhpe25:excel-set-cell cells row-index col
          (* (if accumulated-p curb-acc-straight value) linear-factor))
        (setq col (1+ col)
              value (qhpe23:curb-row-value curb-row 'CURVO)
              curb-acc-curved (+ curb-acc-curved value))
        (qhpe25:excel-set-cell cells row-index col
          (* (if accumulated-p curb-acc-curved value) linear-factor))))
    (setq row-index (1+ row-index)))

  (setq final-row row-index)
  (qhpe25:excel-set-cell cells final-row 1 "TOTAL")
  (setq col 3)
  (foreach layer layers
    (qhpe25:excel-set-cell cells final-row col
      (* (qhpe25:layer-total-area area-rows layer) linear-factor linear-factor))
    (setq col (1+ col)))
  (if has-curb
    (progn
      (qhpe25:excel-set-cell cells final-row col
        (* (qhpe25:data-get 'CURB-TOTAL-STRAIGHT data) linear-factor))
      (setq col (1+ col))
      (qhpe25:excel-set-cell cells final-row col
        (* (qhpe25:data-get 'CURB-TOTAL-CURVED data) linear-factor))))

  (setq last-column (qhpe25:column-letter (length headers)))
  (if (> (length headers) 2)
    (progn
      (setq number-range
        (qhpe25:excel-range sheet
          (strcat "C3:" last-column (itoa final-row))))
      (vlax-put-property number-range 'NumberFormat "0.00")
      (vlax-release-object number-range)))
  (qhpe25:excel-style-sheet sheet final-row (length headers) title)
  (vlax-release-object cells)
)

(defun qhpe25:excel-fill-summary-sheet
  (sheet rows / cells row-index final-row number-range)
  (setq cells (vlax-get-property sheet 'Cells))
  (qhpe25:excel-set-cell cells 1 1 "RESUMO DOS SERVI\U+00C7OS")
  (qhpe25:excel-set-cell cells 2 1 "SERVI\U+00C7O")
  (qhpe25:excel-set-cell cells 2 2 "UNIDADE")
  (qhpe25:excel-set-cell cells 2 3 "TOTAL")
  (setq row-index 3)
  (foreach row rows
    (qhpe25:excel-set-cell cells row-index 1 (nth 0 row))
    (qhpe25:excel-set-cell cells row-index 2
      (if (= (nth 1 row) "m\\U+00B2") "m\U+00B2" (nth 1 row)))
    (qhpe25:excel-set-cell cells row-index 3 (nth 4 row))
    (setq row-index (1+ row-index)))
  (setq final-row (1- row-index)
        number-range (qhpe25:excel-range sheet (strcat "C3:C" (itoa final-row))))
  (vlax-put-property number-range 'NumberFormat "0.00")
  (vlax-release-object number-range)
  (qhpe25:excel-style-sheet sheet final-row 3 "RESUMO DOS SERVI\U+00C7OS")
  (vlax-release-object cells)
)

(defun qhpe25:export-excel
  (data start-station station-step linear-factor / excel books book sheets
    sheet1 sheet2 sheet3 summary-rows result)
  (princ "\nAbrindo o Excel e montando a planilha formatada...")
  (setq result
    (vl-catch-all-apply
      (function
        (lambda ()
          (setq excel (vlax-get-or-create-object "Excel.Application")
                books (vlax-get-property excel 'Workbooks)
                book (vlax-invoke-method books 'Add)
                sheets (vlax-get-property book 'Worksheets))
          (while (< (vlax-get-property sheets 'Count) 3)
            (vlax-invoke-method sheets 'Add))
          (setq sheet1 (vlax-get-property sheets 'Item 1)
                sheet2 (vlax-get-property sheets 'Item 2)
                sheet3 (vlax-get-property sheets 'Item 3))
          (vlax-put-property sheet1 'Name "Por Estaca")
          (vlax-put-property sheet2 'Name "Acumulados")
          (vlax-put-property sheet3 'Name "Resumo")
          (qhpe25:excel-fill-station-sheet sheet1 "QUANTITATIVOS POR ESTACA"
            data start-station station-step linear-factor nil)
          (qhpe25:excel-fill-station-sheet sheet2 "ACUMULADOS POR ESTACA"
            data start-station station-step linear-factor T)
          (setq summary-rows (qhpe25:summary-rows data linear-factor))
          (qhpe25:excel-fill-summary-sheet sheet3 summary-rows)
          (vlax-put-property excel 'Visible :vlax-true)
          (vlax-invoke-method sheet1 'Activate)
          T))))
  (foreach object (list sheet1 sheet2 sheet3 sheets book books excel)
    (if (and object (not (vlax-object-released-p object)))
      (vlax-release-object object)))
  (gc)
  (if (vl-catch-all-error-p result)
    (progn
      (princ (strcat "\nFalha ao exportar para Excel: "
        (vl-catch-all-error-message result)))
      nil)
    (progn
      (princ "\nPlanilha criada no Excel. Use Salvar Como para escolher o arquivo.")
      T))
)

(defun qhpe25:generate-output
  (doc space axis hatch-ss curb-layer station-step start-station search-width
    linear-factor insertion text-height band-height summary-point export-choice
    / data total layers diagram-data area-rows curb-rows curb-straight
      curb-curved has-curb geometry actual-summary summary-rows old-count
      draw-result objects group-name metadata group)

  (setq data
    (qhpe25:compute-data axis hatch-ss curb-layer station-step start-station
      search-width linear-factor space))
  (if (null data)
    nil
    (progn
      (setq total (qhpe25:data-get 'TOTAL data)
            layers (qhpe25:data-get 'LAYERS data)
            diagram-data (qhpe25:data-get 'DIAGRAM-DATA data)
            area-rows (qhpe25:data-get 'AREA-ROWS data)
            curb-rows (qhpe25:data-get 'CURB-ROWS data)
            curb-straight (qhpe25:data-get 'CURB-STRAIGHT data)
            curb-curved (qhpe25:data-get 'CURB-CURVED data)
            has-curb (qhpe25:data-get 'HAS-CURB data))
      (qhpe:safe-set-clayer qhpe:*diagram-layer-name*)
      (qhpe:cleanup-temp)
      (setq actual-summary
        (if summary-point summary-point
          (qhpe25:automatic-summary-point insertion total station-step layers
            band-height text-height has-curb)))
      (setq old-count (vla-get-Count space))
      (princ "\nCriando o diagrama atualizado...")
      (setq draw-result
        (vl-catch-all-apply 'qhpe23:draw-base-diagram
          (list insertion total layers diagram-data start-station station-step
            band-height text-height qhpe:*diagram-layer-name* curb-layer
            curb-straight curb-curved)))
      (if (vl-catch-all-error-p draw-result)
        (progn
          (princ (strcat "\nFalha no diagrama: "
            (vl-catch-all-error-message draw-result)))
          nil)
        (progn
          (princ "\nInserindo tabelas por estaca...")
          (setq draw-result
            (vl-catch-all-apply 'qhpe23:draw-native-tables
              (list insertion area-rows layers curb-rows start-station
                station-step linear-factor band-height text-height
                qhpe:*diagram-layer-name* has-curb)))
          (if (vl-catch-all-error-p draw-result)
            (progn
              (princ (strcat "\nFalha nas tabelas por estaca: "
                (vl-catch-all-error-message draw-result)))
              nil)
            (progn
              (princ "\nInserindo legenda generica e tabela geral de totais...")
              (setq summary-rows (qhpe25:summary-rows data linear-factor))
              (qhpe25:create-summary-table actual-summary summary-rows text-height
                qhpe:*diagram-layer-name*)
              (setq objects (qhpe25:modelspace-new-objects space old-count)
                    group-name (qhpe25:group-name)
                    metadata
                      (qhpe25:metadata-string axis hatch-ss curb-layer
                        station-step start-station search-width linear-factor
                        insertion text-height band-height actual-summary)
                    group (qhpe25:create-group doc group-name objects metadata)
                    qhpe25:*last-generated-group* group-name)
              (if export-choice
                (qhpe25:export-excel data start-station station-step linear-factor))
              (vla-Regen doc 1)
              (list group group-name data actual-summary)
            )
          )
        )
      )
    )
  )
)

(defun qhpe25:restore-environment
  (doc old-clayer old-cmdecho old-osmode old-error)
  (qhpe:safe-set-clayer old-clayer)
  (vl-catch-all-apply 'setvar (list "CMDECHO" old-cmdecho))
  (vl-catch-all-apply 'setvar (list "OSMODE" old-osmode))
  (vl-catch-all-apply 'vla-EndUndoMark (list doc))
  (setq *error* old-error)
  T
)

(defun c:QHPEGERAR
  (/ *error* old-error acad doc space old-cmdecho old-osmode old-clayer
    axis-selection axis hatch-ss curb-layer station-step start-default
    station-default-text station-text start-station search-width linear-factor
    insertion text-height band-height summary-point export-default export-choice
    result)

  (setq old-error *error*
        acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        old-cmdecho (getvar "CMDECHO")
        old-osmode (getvar "OSMODE")
        old-clayer (getvar "CLAYER"))

  (defun *error* (message)
    (qhpe:cleanup-temp)
    (qhpe25:restore-environment doc old-clayer old-cmdecho old-osmode old-error)
    (if (and message
      (not (wcmatch (strcase message) "*CANCEL*,*QUIT*,*EXIT*")))
      (princ (strcat "\nErro QHPEGERAR: " message)))
    (princ))

  (vla-StartUndoMark doc)
  (setvar "CMDECHO" 0)
  (setvar "OSMODE" 0)
  (qhpe:prepare-temp-layer doc)
  (qhpe:get-layer-object doc qhpe:*diagram-layer-name*)
  (princ "\nQHPEGERAR - Fluxo completo gerenciado")

  (while
    (progn
      (setq axis-selection (entsel "\nSelecione a polilinha do eixo: "))
      (and axis-selection
        (not (qhpe:valid-axis-polyline-p (car axis-selection))))))
  (if (null axis-selection)
    (*error* "Polilinha nao selecionada.")
    (progn
      (setq axis (car axis-selection))
      (princ "\nSelecione as hachuras dos pavimentos:")
      (setq hatch-ss (ssget "_:L" '((0 . "HATCH"))))
      (if (null hatch-ss)
        (*error* "Nenhuma hachura selecionada.")
        (progn
          (setq curb-layer (qhpe25:prompt-curb-layer-saved)
                station-step
                  (qhpe25:prompt-positive-real
                    "\nComprimento de cada estaca nas unidades do desenho"
                    (qhpe25:getenv-real "STEP" qhpe:*interval-default*))
                start-default (qhpe25:getenv-real "START" 0.0)
                station-default-text
                  (qhpe:format-station-diagram start-default station-step)
                station-text
                  (getstring T
                    (strcat "\nEstaca no inicio da polilinha <"
                      station-default-text ">: ")))
          (if (= station-text "") (setq station-text station-default-text))
          (setq start-station (qhpe:parse-station station-text station-step)
                search-width
                  (qhpe25:prompt-positive-real
                    "\nLargura de busca para o lado ESQUERDO da polilinha"
                    (qhpe25:getenv-real "WIDTH" qhpe:*width-default*))
                qhpe:*right-width-current*
                  (qhpe25:prompt-positive-real
                    "\nLargura de busca para o lado DIREITO da polilinha"
                    (qhpe25:getenv-real "WIDTH_RIGHT" search-width))
                qhpe:*right-width-default* qhpe:*right-width-current*
                linear-factor
                  (qhpe25:prompt-positive-real
                    "\nFator linear para converter a unidade do desenho em metros"
                    (qhpe25:getenv-real "FACTOR" qhpe:*linear-factor-default*))
                insertion
                  (getpoint "\nPonto inicial inferior do diagrama: "))
          (if (null insertion)
            (*error* "Ponto inicial nao informado.")
            (progn
              (setq text-height
                (qhpe25:prompt-positive-real
                  "\nAltura dos textos"
                  (qhpe25:getenv-real "TEXT"
                    (if qhpe:*diagram-text-height-default*
                      qhpe:*diagram-text-height-default* (* station-step 0.025))))
                band-height
                  (qhpe25:prompt-positive-real
                    "\nAltura de cada faixa de pavimento"
                    (qhpe25:getenv-real "BAND"
                      (if qhpe:*diagram-band-height-default*
                        qhpe:*diagram-band-height-default* (* station-step 0.12)))))
              (setq summary-point
                (getpoint
                  "\nPonto INFERIOR esquerdo da legenda/totais ou Enter para automatico: "))
              (setq export-default
                (= (strcase (qhpe25:getenv-string "EXPORT" "NAO")) "SIM")
                    export-choice
                      (qhpe25:prompt-yes-no
                        "\nAbrir planilha formatada no Excel ao concluir?"
                        export-default))
              (if
                (qhpe25:preflight doc axis hatch-ss curb-layer station-step
                  search-width linear-factor text-height band-height)
                (progn
                  (setq result
                    (qhpe25:generate-output doc space axis hatch-ss curb-layer
                      station-step start-station search-width linear-factor
                      insertion text-height band-height summary-point export-choice))
                  (if result
                    (progn
                      (qhpe25:save-settings station-step start-station search-width
                        linear-factor text-height band-height curb-layer export-choice)
                      (qhpe25:restore-environment doc old-clayer old-cmdecho
                        old-osmode old-error)
                      (princ
                        (strcat "\nConjunto gerado e registrado no grupo "
                          (cadr result) ".")))
                    (*error* "Nao foi possivel concluir a geracao.")))
                (*error* "Operacao cancelada na verificacao previa."))
            )
          )
        )
      )
    )
  )
  (princ)
)

(defun qhpe25:metadata-value (key metadata)
  (cdr (assoc key metadata))
)

(defun qhpe25:resolve-metadata-sources
  (metadata / axis hatch-ss curb-layer)
  (setq axis (handent (qhpe25:metadata-value 'A metadata))
        hatch-ss
          (qhpe25:ss-from-handles (qhpe25:metadata-value 'H metadata) "HATCH")
        curb-layer (qhpe25:metadata-value 'C metadata))
  (if (= curb-layer "") (setq curb-layer nil))
  (list axis hatch-ss curb-layer)
)

(defun c:QHPEUPDATE
  (/ *error* old-error acad doc space old-cmdecho old-osmode old-clayer
    selection group metadata sources axis hatch-ss curb-layer result new-result)
  (setq old-error *error*
        acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        old-cmdecho (getvar "CMDECHO")
        old-osmode (getvar "OSMODE")
        old-clayer (getvar "CLAYER"))
  (defun *error* (message)
    (qhpe:cleanup-temp)
    (qhpe25:restore-environment doc old-clayer old-cmdecho old-osmode old-error)
    (if (and message
      (not (wcmatch (strcase message) "*CANCEL*,*QUIT*,*EXIT*")))
      (princ (strcat "\nErro QHPEUPDATE: " message)))
    (princ))
  (vla-StartUndoMark doc)
  (setvar "CMDECHO" 0)
  (setvar "OSMODE" 0)
  (qhpe:prepare-temp-layer doc)
  (setq selection (entsel "\nSelecione qualquer objeto de um conjunto QHPE: "))
  (if (null selection)
    (*error* "Objeto nao selecionado.")
    (progn
      (setq group (qhpe25:find-group-for-entity doc (car selection)))
      (if (null group)
        (*error* "O objeto nao pertence a um conjunto atualizavel do QHPE.")
        (progn
          (setq metadata (qhpe25:group-metadata group)
                sources (qhpe25:resolve-metadata-sources metadata)
                axis (nth 0 sources)
                hatch-ss (nth 1 sources)
                curb-layer (nth 2 sources)
                qhpe:*right-width-current*
                  (if (numberp (qhpe25:metadata-value 'WR metadata))
                    (qhpe25:metadata-value 'WR metadata)
                    (qhpe25:metadata-value 'W metadata))
                qhpe:*right-width-default* qhpe:*right-width-current*)
          (if (or (null metadata) (null axis) (null hatch-ss))
            (*error*
              "As fontes originais nao foram encontradas. O eixo ou hachuras podem ter sido apagados.")
            (if
              (qhpe25:preflight doc axis hatch-ss curb-layer
                (qhpe25:metadata-value 'S metadata)
                (qhpe25:metadata-value 'W metadata)
                (qhpe25:metadata-value 'F metadata)
                (qhpe25:metadata-value 'TH metadata)
                (qhpe25:metadata-value 'BH metadata))
              (progn
                ;; Gera primeiro. O conjunto antigo so e removido depois do sucesso.
                (setq new-result
                  (qhpe25:generate-output doc space axis hatch-ss curb-layer
                    (qhpe25:metadata-value 'S metadata)
                    (qhpe25:metadata-value 'ST metadata)
                    (qhpe25:metadata-value 'W metadata)
                    (qhpe25:metadata-value 'F metadata)
                    (qhpe25:metadata-value 'I metadata)
                    (qhpe25:metadata-value 'TH metadata)
                    (qhpe25:metadata-value 'BH metadata)
                    (qhpe25:metadata-value 'L metadata)
                    nil))
                (if new-result
                  (progn
                    (qhpe25:delete-group-and-objects group)
                    (qhpe25:restore-environment doc old-clayer old-cmdecho
                      old-osmode old-error)
                    (princ "\nConjunto atualizado com sucesso."))
                  (*error* "A atualizacao falhou; o conjunto anterior foi preservado.")))
              (*error* "Atualizacao cancelada na verificacao previa."))
          )
        )
      )
    )
  )
  (princ)
)

(defun c:QHPEEXPORT
  (/ *error* old-error acad doc space old-cmdecho old-osmode old-clayer
    selection group metadata sources axis hatch-ss curb-layer data)
  (setq old-error *error*
        acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        old-cmdecho (getvar "CMDECHO")
        old-osmode (getvar "OSMODE")
        old-clayer (getvar "CLAYER"))
  (defun *error* (message)
    (qhpe:cleanup-temp)
    (qhpe25:restore-environment doc old-clayer old-cmdecho old-osmode old-error)
    (if (and message
      (not (wcmatch (strcase message) "*CANCEL*,*QUIT*,*EXIT*")))
      (princ (strcat "\nErro QHPEEXPORT: " message)))
    (princ))
  (vla-StartUndoMark doc)
  (setvar "CMDECHO" 0)
  (setvar "OSMODE" 0)
  (qhpe:prepare-temp-layer doc)
  (setq selection (entsel "\nSelecione qualquer objeto de um conjunto QHPE: "))
  (if (null selection)
    (*error* "Objeto nao selecionado.")
    (progn
      (setq group (qhpe25:find-group-for-entity doc (car selection)))
      (if (null group)
        (*error* "O objeto nao pertence a um conjunto exportavel do QHPE.")
        (progn
          (setq metadata (qhpe25:group-metadata group)
                sources (qhpe25:resolve-metadata-sources metadata)
                axis (nth 0 sources)
                hatch-ss (nth 1 sources)
                curb-layer (nth 2 sources)
                qhpe:*right-width-current*
                  (if (numberp (qhpe25:metadata-value 'WR metadata))
                    (qhpe25:metadata-value 'WR metadata)
                    (qhpe25:metadata-value 'W metadata))
                qhpe:*right-width-default* qhpe:*right-width-current*)
          (if (or (null metadata) (null axis) (null hatch-ss))
            (*error* "Nao foi possivel localizar as fontes originais.")
            (progn
              (setq data
                (qhpe25:compute-data axis hatch-ss curb-layer
                  (qhpe25:metadata-value 'S metadata)
                  (qhpe25:metadata-value 'ST metadata)
                  (qhpe25:metadata-value 'W metadata)
                  (qhpe25:metadata-value 'F metadata)
                  space))
              (qhpe:cleanup-temp)
              (if data
                (progn
                  (qhpe25:export-excel data
                    (qhpe25:metadata-value 'ST metadata)
                    (qhpe25:metadata-value 'S metadata)
                    (qhpe25:metadata-value 'F metadata))
                  (qhpe25:restore-environment doc old-clayer old-cmdecho
                    old-osmode old-error))
                (*error* "Nao foi possivel recalcular os quantitativos.")))
          )
        )
      )
    )
  )
  (princ)
)

;;; ================================================================
;;; QHPE - DEFINICOES ATUAIS
;;; ================================================================

(setq qhpe:*version* "ATUAL")
(setq qhpe:*diagram-layer-name* "QHPE_ANOTACOES")
(setq qhpe:*table-layer-name* "QHPE_TABELAS")
(setq qhpe:*summary-layer-name* "QHPE_RESUMO")

;;; Formata a parte decimal sem depender de DIMZIN.
(defun qhpe:fixed-2 (value / scaled whole cents)
  (setq scaled (fix (+ (* (abs value) 100.0) 0.5))
        whole (fix (/ scaled 100))
        cents (rem scaled 100))
  (strcat
    (if (< value 0.0) "-" "")
    (itoa whole) "."
    (if (< cents 10) "0" "")
    (itoa cents))
)

;;; Formato unico: 0+0.00, 1+0.00, 5+14.90 etc.
(defun qhpe:format-station-current (value interval / stake remainder tol)
  (setq tol (* interval 1.0e-8)
        stake (fix (/ value interval))
        remainder (- value (* stake interval)))
  (if (>= remainder (- interval tol))
    (setq stake (1+ stake) remainder 0.0))
  (if (< (abs remainder) tol) (setq remainder 0.0))
  (strcat (itoa stake) "+" (qhpe:fixed-2 remainder))
)

(defun qhpe:format-station (value interval)
  (qhpe:format-station-current value interval))

(defun qhpe:format-station-diagram (value interval)
  (qhpe:format-station-current value interval))

;;; Textos para Excel montados em tempo de execucao. Isso evita mojibake
;;; quando o AutoLISP e carregado com configuracao ANSI ou Unicode diferente.
(defun qhpe:excel-servico ()
  (strcat "SERVI" (chr 199) "O"))
(defun qhpe:excel-servicos ()
  (strcat "SERVI" (chr 199) "OS"))
(defun qhpe:excel-m2 ()
  (strcat "m" (chr 178)))

(defun qhpe25:excel-apply-number-format (range / result)
  (setq result
    (vl-catch-all-apply 'vlax-put-property
      (list range 'NumberFormatLocal "#.##0,00")))
  (if (vl-catch-all-error-p result)
    (vl-catch-all-apply 'vlax-put-property
      (list range 'NumberFormat "#,##0.00")))
)

;;; Os pavimentos permanecem exatamente nos layers das hachuras selecionadas.
;;; Para meio-fio, o trecho reto usa o proprio layer selecionado. O trecho
;;; curvo fica em <LAYER>_CURVO, preservando a separacao e a cor magenta.
(defun qhpe24:curb-output-layers
  (source-layer-name / acad doc source-layer source-result curved-layer)
  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        curved-layer (strcat source-layer-name "_CURVO")
        qhpe24:*curb-layer-straight* source-layer-name
        qhpe24:*curb-layer-curved* curved-layer)
  (setq source-result
    (vl-catch-all-apply 'vla-Item
      (list (vla-get-Layers doc) source-layer-name)))
  (if (not (vl-catch-all-error-p source-result))
    (progn
      (setq source-layer source-result)
      (vl-catch-all-apply 'vla-put-LayerOn (list source-layer :vlax-true))
      (vl-catch-all-apply 'vla-put-Freeze (list source-layer :vlax-false))
      (vl-catch-all-apply 'vla-put-Lock (list source-layer :vlax-false))))
  (qhpe24:ensure-output-layer doc curved-layer 6)
  (list source-layer-name curved-layer)
)

;;; Recalculo estruturado. Tambem prepara os layers de meio-fio derivados do
;;; layer selecionado para que legenda, diagrama e exportacao usem os mesmos.
(defun qhpe25:compute-data
  (axis hatch-ss curb-layer station-step start-station search-width linear-factor
    space / total counter hatch hatch-records records layers projected-result
    projected diagram-data area-rows ranges curb-ss curb-analysis curb-rows
    curb-straight curb-curved curb-total-straight curb-total-curved)
  (setq total (qhpe:curve-length axis)
        counter 0
        records nil)
  (princ "\nGerando limites e verificando ilhas das hachuras...")
  (repeat (sslength hatch-ss)
    (setq hatch (ssname hatch-ss counter)
          hatch-records (qhpe:make-hatch-records hatch space))
    (if hatch-records (setq records (append hatch-records records)))
    (setq counter (1+ counter)))
  (if (null records)
    nil
    (progn
      (setq layers (qhpe:unique-layers records))
      (princ "\nCalculando areas por estaca...")
      (setq area-rows
        (qhpe:process-intervals axis total records layers start-station
          station-step search-width linear-factor space))
      (setq layers (qhpe:sort-layers-by-total layers area-rows))
      (princ "\nCalculando inicios e finais dos pavimentos...")
      (setq projected-result
        (vl-catch-all-apply 'qhpe:projection-intervals-by-layer
          (list axis total records station-step)))
      (if (vl-catch-all-error-p projected-result)
        (setq projected nil)
        (setq projected projected-result))
      (setq diagram-data (qhpe2:merged-projections layers projected))
      (setq ranges (qhpe23:station-ranges total start-station station-step)
            curb-ss (qhpe23:curb-selection-set curb-layer))
      (if curb-ss
        (progn
          (qhpe24:curb-output-layers curb-layer)
          (princ "\nCalculando meios-fios retos e curvos...")
          (setq curb-analysis
            (qhpe23:analyze-curbs axis total curb-ss ranges station-step)
                curb-rows (nth 0 curb-analysis)
                curb-straight (nth 1 curb-analysis)
                curb-curved (nth 2 curb-analysis)
                curb-total-straight (nth 3 curb-analysis)
                curb-total-curved (nth 4 curb-analysis)))
        (setq curb-rows nil curb-straight nil curb-curved nil
              curb-total-straight 0.0 curb-total-curved 0.0))
      (list
        (cons 'TOTAL total)
        (cons 'LAYERS layers)
        (cons 'DIAGRAM-DATA diagram-data)
        (cons 'AREA-ROWS area-rows)
        (cons 'CURB-ROWS curb-rows)
        (cons 'CURB-STRAIGHT curb-straight)
        (cons 'CURB-CURVED curb-curved)
        (cons 'CURB-TOTAL-STRAIGHT curb-total-straight)
        (cons 'CURB-TOTAL-CURVED curb-total-curved)
        (cons 'CURB-SOURCE-LAYER curb-layer)
        (cons 'HAS-CURB (if curb-ss T nil))))))

(defun qhpe25:summary-rows
  (data linear-factor / rows layers area-rows layer total has-curb)
  (setq rows nil
        layers (qhpe25:data-get 'LAYERS data)
        area-rows (qhpe25:data-get 'AREA-ROWS data)
        has-curb (qhpe25:data-get 'HAS-CURB data))
  (foreach layer layers
    (setq total
      (* (qhpe25:layer-total-area area-rows layer)
        linear-factor linear-factor))
    (setq rows
      (append rows
        (list
          (list layer "m\\U+00B2" (qhpe:format-number total 2)
            layer total "AREA")))))
  (if has-curb
    (setq rows
      (append rows
        (list
          (list qhpe23:*curb-label-straight* "m"
            (qhpe:format-number
              (* (qhpe25:data-get 'CURB-TOTAL-STRAIGHT data) linear-factor) 2)
            qhpe24:*curb-layer-straight*
            (* (qhpe25:data-get 'CURB-TOTAL-STRAIGHT data) linear-factor)
            "LINEAR")
          (list qhpe23:*curb-label-curved* "m"
            (qhpe:format-number
              (* (qhpe25:data-get 'CURB-TOTAL-CURVED data) linear-factor) 2)
            qhpe24:*curb-layer-curved*
            (* (qhpe25:data-get 'CURB-TOTAL-CURVED data) linear-factor)
            "LINEAR")))))
  rows)

(defun qhpe25:excel-fill-station-sheet
  (sheet title data start-station station-step linear-factor accumulated-p
    / cells layers area-rows curb-rows has-curb headers col row-index data-row
    layer value accum-alist curb-acc-straight curb-acc-curved curb-row final-row
    last-column number-range)
  (setq cells (vlax-get-property sheet 'Cells)
        layers (qhpe25:data-get 'LAYERS data)
        area-rows (qhpe25:data-get 'AREA-ROWS data)
        curb-rows (qhpe25:data-get 'CURB-ROWS data)
        has-curb (qhpe25:data-get 'HAS-CURB data)
        headers (append (list "ESTACA INICIAL" "ESTACA FINAL") layers
                  (if has-curb
                    (list qhpe23:*curb-label-straight* qhpe23:*curb-label-curved*)
                    nil)))
  (qhpe25:excel-set-cell cells 1 1 title)
  (setq col 1)
  (foreach header headers
    (qhpe25:excel-set-cell cells 2 col
      (cond
        ((member header layers) (strcat header " (" (qhpe:excel-m2) ")"))
        ((= header qhpe23:*curb-label-straight*) (strcat header " (m)"))
        ((= header qhpe23:*curb-label-curved*) (strcat header " (m)"))
        (T header)))
    (setq col (1+ col)))
  (setq row-index 3 accum-alist nil curb-acc-straight 0.0 curb-acc-curved 0.0)
  (foreach data-row area-rows
    (qhpe25:excel-set-cell cells row-index 1
      (qhpe:format-station-diagram (+ start-station (nth 4 data-row)) station-step))
    (qhpe25:excel-set-cell cells row-index 2
      (qhpe:format-station-diagram (+ start-station (nth 5 data-row)) station-step))
    (setq col 3)
    (foreach layer layers
      (setq value (max 0.0 (qhpe:row-area (nth 3 data-row) layer))
            accum-alist (qhpe:update-accumulated accum-alist layer value))
      (qhpe25:excel-set-cell cells row-index col
        (* (if accumulated-p (cdr (assoc layer accum-alist)) value)
          linear-factor linear-factor))
      (setq col (1+ col)))
    (if has-curb
      (progn
        (setq curb-row (nth (- row-index 3) curb-rows)
              value (qhpe23:curb-row-value curb-row 'RETO)
              curb-acc-straight (+ curb-acc-straight value))
        (qhpe25:excel-set-cell cells row-index col
          (* (if accumulated-p curb-acc-straight value) linear-factor))
        (setq col (1+ col)
              value (qhpe23:curb-row-value curb-row 'CURVO)
              curb-acc-curved (+ curb-acc-curved value))
        (qhpe25:excel-set-cell cells row-index col
          (* (if accumulated-p curb-acc-curved value) linear-factor))))
    (setq row-index (1+ row-index)))
  (setq final-row row-index)
  (qhpe25:excel-set-cell cells final-row 1 "TOTAL")
  (setq col 3)
  (foreach layer layers
    (qhpe25:excel-set-cell cells final-row col
      (* (qhpe25:layer-total-area area-rows layer) linear-factor linear-factor))
    (setq col (1+ col)))
  (if has-curb
    (progn
      (qhpe25:excel-set-cell cells final-row col
        (* (qhpe25:data-get 'CURB-TOTAL-STRAIGHT data) linear-factor))
      (setq col (1+ col))
      (qhpe25:excel-set-cell cells final-row col
        (* (qhpe25:data-get 'CURB-TOTAL-CURVED data) linear-factor))))
  (setq last-column (qhpe25:column-letter (length headers)))
  (if (> (length headers) 2)
    (progn
      (setq number-range
        (qhpe25:excel-range sheet
          (strcat "C3:" last-column (itoa final-row))))
      (qhpe25:excel-apply-number-format number-range)
      (vlax-release-object number-range)))
  (qhpe25:excel-style-sheet sheet final-row (length headers) title)
  (vlax-release-object cells))

(defun qhpe25:excel-fill-summary-sheet
  (sheet rows / cells row-index final-row number-range title service unit)
  (setq cells (vlax-get-property sheet 'Cells)
        title (strcat "RESUMO DOS " (qhpe:excel-servicos))
        service (qhpe:excel-servico)
        unit "UNIDADE")
  (qhpe25:excel-set-cell cells 1 1 title)
  (qhpe25:excel-set-cell cells 2 1 service)
  (qhpe25:excel-set-cell cells 2 2 unit)
  (qhpe25:excel-set-cell cells 2 3 "TOTAL")
  (setq row-index 3)
  (foreach row rows
    (qhpe25:excel-set-cell cells row-index 1 (nth 0 row))
    (qhpe25:excel-set-cell cells row-index 2
      (if (= (nth 1 row) "m\\U+00B2") (qhpe:excel-m2) (nth 1 row)))
    (qhpe25:excel-set-cell cells row-index 3 (nth 4 row))
    (setq row-index (1+ row-index)))
  (setq final-row (1- row-index)
        number-range (qhpe25:excel-range sheet (strcat "C3:C" (itoa final-row))))
  (qhpe25:excel-apply-number-format number-range)
  (vlax-release-object number-range)
  (qhpe25:excel-style-sheet sheet final-row 3 title)
  (vlax-release-object cells))

;;; Gera o conjunto completo usando layers proprios para anotacoes, tabelas e
;;; resumo. As faixas de pavimento continuam nos layers originais.
(defun qhpe25:generate-output
  (doc space axis hatch-ss curb-layer station-step start-station search-width
    linear-factor insertion text-height band-height summary-point export-choice
    / data total layers diagram-data area-rows curb-rows curb-straight
      curb-curved has-curb actual-summary summary-rows old-count draw-result
      objects group-name metadata group)
  (foreach layer-name
    (list qhpe:*diagram-layer-name* qhpe:*table-layer-name* qhpe:*summary-layer-name*)
    (qhpe:get-layer-object doc layer-name))
  (setq data
    (qhpe25:compute-data axis hatch-ss curb-layer station-step start-station
      search-width linear-factor space))
  (if (null data)
    nil
    (progn
      (setq total (qhpe25:data-get 'TOTAL data)
            layers (qhpe25:data-get 'LAYERS data)
            diagram-data (qhpe25:data-get 'DIAGRAM-DATA data)
            area-rows (qhpe25:data-get 'AREA-ROWS data)
            curb-rows (qhpe25:data-get 'CURB-ROWS data)
            curb-straight (qhpe25:data-get 'CURB-STRAIGHT data)
            curb-curved (qhpe25:data-get 'CURB-CURVED data)
            has-curb (qhpe25:data-get 'HAS-CURB data))
      (qhpe:safe-set-clayer qhpe:*diagram-layer-name*)
      (qhpe:cleanup-temp)
      (setq actual-summary
        (if summary-point summary-point
          (qhpe25:automatic-summary-point insertion total station-step layers
            band-height text-height has-curb)))
      (setq old-count (vla-get-Count space))
      (princ "\nCriando o diagrama...")
      (setq draw-result
        (vl-catch-all-apply 'qhpe23:draw-base-diagram
          (list insertion total layers diagram-data start-station station-step
            band-height text-height qhpe:*diagram-layer-name* curb-layer
            curb-straight curb-curved)))
      (if (vl-catch-all-error-p draw-result)
        nil
        (progn
          (princ "\nInserindo tabelas por estaca...")
          (setq draw-result
            (vl-catch-all-apply 'qhpe23:draw-native-tables
              (list insertion area-rows layers curb-rows start-station
                station-step linear-factor band-height text-height
                qhpe:*table-layer-name* has-curb)))
          (if (vl-catch-all-error-p draw-result)
            nil
            (progn
              (princ "\nInserindo legenda e totais...")
              (setq summary-rows (qhpe25:summary-rows data linear-factor))
              (qhpe25:create-summary-table actual-summary summary-rows text-height
                qhpe:*summary-layer-name*)
              (setq objects (qhpe25:modelspace-new-objects space old-count)
                    group-name (qhpe25:group-name)
                    metadata
                      (qhpe25:metadata-string axis hatch-ss curb-layer
                        station-step start-station search-width linear-factor
                        insertion text-height band-height actual-summary)
                    group (qhpe25:create-group doc group-name objects metadata)
                    qhpe25:*last-generated-group* group-name)
              (if export-choice
                (qhpe25:export-excel data start-station station-step linear-factor))
              (vla-Regen doc 1)
              (list group group-name data actual-summary))))))))

;;; Comando separado para inserir apenas legenda e tabela geral de totais.
(defun c:QHPERESUMO
  (/ *error* old-error acad doc space old-cmdecho old-osmode old-clayer
    axis-selection axis hatch-ss curb-layer station-step start-default
    station-default-text station-text start-station search-width linear-factor
    insertion text-height data rows)
  (setq old-error *error*
        acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        old-cmdecho (getvar "CMDECHO")
        old-osmode (getvar "OSMODE")
        old-clayer (getvar "CLAYER"))
  (defun *error* (message)
    (qhpe:cleanup-temp)
    (qhpe25:restore-environment doc old-clayer old-cmdecho old-osmode old-error)
    (if (and message
      (not (wcmatch (strcase message) "*CANCEL*,*QUIT*,*EXIT*")))
      (princ (strcat "\nErro QHPERESUMO: " message)))
    (princ))
  (vla-StartUndoMark doc)
  (setvar "CMDECHO" 0)
  (setvar "OSMODE" 0)
  (qhpe:prepare-temp-layer doc)
  (qhpe:get-layer-object doc qhpe:*summary-layer-name*)
  (setq axis-selection (entsel "\nSelecione a polilinha do eixo: "))
  (if (or (null axis-selection)
          (not (qhpe:valid-axis-polyline-p (car axis-selection))))
    (*error* "Polilinha valida nao selecionada.")
    (progn
      (setq axis (car axis-selection))
      (princ "\nSelecione as hachuras: ")
      (setq hatch-ss (ssget "_:L" '((0 . "HATCH"))))
      (if (null hatch-ss)
        (*error* "Nenhuma hachura selecionada.")
        (progn
          (setq curb-layer (qhpe25:prompt-curb-layer-saved)
                station-step
                  (qhpe25:prompt-positive-real
                    "\nComprimento de cada estaca nas unidades do desenho"
                    (qhpe25:getenv-real "STEP" qhpe:*interval-default*))
                start-default (qhpe25:getenv-real "START" 0.0)
                station-default-text
                  (qhpe:format-station-diagram start-default station-step)
                station-text
                  (getstring T
                    (strcat "\nEstaca no inicio da polilinha <"
                      station-default-text ">: ")))
          (if (= station-text "") (setq station-text station-default-text))
          (setq start-station (qhpe:parse-station station-text station-step)
                search-width
                  (qhpe25:prompt-positive-real
                    "\nLargura de busca para o lado ESQUERDO da polilinha"
                    (qhpe25:getenv-real "WIDTH" qhpe:*width-default*))
                qhpe:*right-width-current*
                  (qhpe25:prompt-positive-real
                    "\nLargura de busca para o lado DIREITO da polilinha"
                    (qhpe25:getenv-real "WIDTH_RIGHT" search-width))
                qhpe:*right-width-default* qhpe:*right-width-current*
                linear-factor
                  (qhpe25:prompt-positive-real
                    "\nFator linear para converter a unidade do desenho em metros"
                    (qhpe25:getenv-real "FACTOR" qhpe:*linear-factor-default*))
                insertion (getpoint "\nPonto inferior esquerdo da legenda/totais: ")
                text-height
                  (qhpe25:prompt-positive-real
                    "\nAltura dos textos"
                    (qhpe25:getenv-real "TEXT"
                      (if qhpe:*diagram-text-height-default*
                        qhpe:*diagram-text-height-default* (* station-step 0.025)))))
          (if (null insertion)
            (*error* "Ponto de insercao nao informado.")
            (progn
              (setq data
                (qhpe25:compute-data axis hatch-ss curb-layer station-step
                  start-station search-width linear-factor space))
              (qhpe:cleanup-temp)
              (if data
                (progn
                  (setq rows (qhpe25:summary-rows data linear-factor))
                  (qhpe25:create-summary-table insertion rows text-height
                    qhpe:*summary-layer-name*)
                  (vla-Regen doc 1)
                  (qhpe25:restore-environment doc old-clayer old-cmdecho
                    old-osmode old-error)
                  (princ "\nLegenda e totais inseridos."))
                (*error* "Nao foi possivel calcular os quantitativos."))))))))
  (princ))

;;; ================================================================
;;; CORRECAO DE ESTABILIDADE - ANALISE DE MEIOS-FIOS
;;;
;;; - Evita amostragem fixa excessiva que podia causar Access Violation;
;;; - Usa subdivisao adaptativa apenas nas divisas de estaca e da faixa;
;;; - Respeita a largura de busca lateral informada;
;;; - Ignora elementos graficos gerados pelo proprio QHPE;
;;; - Mantem os elementos do diagrama nos layers dos objetos selecionados.
;;; ================================================================

(setq qhpe:*version* "FINAL")
(setq qhpe26:*xdata-app* "QHPE_OUTPUT")

(defun qhpe26:ensure-regapp ()
  (if (not (tblsearch "APPID" qhpe26:*xdata-app*))
    (regapp qhpe26:*xdata-app*)
  )
  qhpe26:*xdata-app*
)

(defun qhpe26:tag-output (ent kind / data)
  (if (and ent (entget ent))
    (progn
      (qhpe26:ensure-regapp)
      (setq data (entget ent))
      (if (null (assoc -3 (entget ent (list qhpe26:*xdata-app*))))
        (entmod
          (append data
            (list
              (list -3
                (list qhpe26:*xdata-app*
                  (cons 1000 (if kind kind "DIAGRAMA"))))))))
      ent)
    ent)
)

(defun qhpe26:output-entity-p (ent / data)
  (and ent
       (setq data (entget ent (list qhpe26:*xdata-app*)))
       (assoc -3 data))
)

(defun qhpe26:lwpoly-vertex-count (ent / data)
  (setq data (entget ent))
  (cond
    ((cdr (assoc 90 data)))
    (0)))

(defun qhpe26:lwpoly-all-zero-bulges-p (ent / data pair ok)
  (setq data (entget ent)
        ok T)
  (foreach pair data
    (if (and (= (car pair) 42) (> (abs (cdr pair)) 1.0e-10))
      (setq ok nil)))
  ok)

;;; Reconhece os retangulos de contorno criados pelas versoes anteriores,
;;; mesmo quando eles ainda nao possuem XData.
(defun qhpe26:legacy-band-rectangle-p (ent / data typ flags)
  (setq data (entget ent)
        typ (cdr (assoc 0 data))
        flags (cond ((cdr (assoc 70 data))) (0)))
  (and (= typ "LWPOLYLINE")
       (/= 0 (logand flags 1))
       (= (qhpe26:lwpoly-vertex-count ent) 4)
       (qhpe26:lwpoly-all-zero-bulges-p ent))
)

(defun qhpe26:valid-curb-source-p (ent / data typ flags length)
  (if (and ent (setq data (entget ent)))
    (progn
      (setq typ (cdr (assoc 0 data))
            flags (cond ((cdr (assoc 70 data))) (0))
            length (qhpe:curve-length ent))
      (and
        (member typ '("LWPOLYLINE" "POLYLINE" "LINE" "ARC"))
        (not (qhpe26:output-entity-p ent))
        (not (qhpe26:legacy-band-rectangle-p ent))
        (not (and (= typ "POLYLINE") (/= 0 (logand flags 8))))
        (numberp length)
        (> length 1.0e-8)))
    nil)
)

;;; Sobrescreve a selecao para remover objetos de saida do proprio diagrama.
(defun qhpe23:curb-selection-set (layer / filter raw result index ent)
  (if layer
    (progn
      (setq filter
        (list
          (cons 0 "LWPOLYLINE,POLYLINE,LINE,ARC")
          (cons 8 layer)
          (cons 410 (getvar "CTAB"))))
      (setq raw (ssget "_X" filter)
            result (ssadd)
            index 0)
      (if raw
        (repeat (sslength raw)
          (setq ent (ssname raw index))
          (if (qhpe26:valid-curb-source-p ent)
            (ssadd ent result))
          (setq index (1+ index))))
      (if (> (sslength result) 0) result nil)))
)

;;; Marca os contornos criados pelo diagrama. O layer continua sendo o layer
;;; original do servico, mas o objeto nao volta a entrar no quantitativo.
(defun qhpe21:make-rectangle
  (q21_x0 q21_y0 q21_x1 q21_y1 q21_layer / ent)
  (setq ent
    (entmakex
      (list
        '(0 . "LWPOLYLINE")
        '(100 . "AcDbEntity")
        (cons 8 q21_layer)
        '(100 . "AcDbPolyline")
        '(90 . 4)
        '(70 . 1)
        (cons 10 (list q21_x0 q21_y0))
        (cons 10 (list q21_x1 q21_y0))
        (cons 10 (list q21_x1 q21_y1))
        (cons 10 (list q21_x0 q21_y1))
        '(210 0.0 0.0 1.0))))
  (qhpe26:tag-output ent "FAIXA_DIAGRAMA")
)

(defun qhpe26:distance-2d (p1 p2)
  (sqrt
    (+
      (expt (- (car p1) (car p2)) 2.0)
      (expt (- (cadr p1) (cadr p2)) 2.0))))

;;; Retorna: (distancia-no-eixo afastamento-lateral)
(defun qhpe26:project-info (axis point / closest station param der vx vy cross distance signed-offset)
  (if point
    (progn
      (setq closest
        (vl-catch-all-apply
          'vlax-curve-getClosestPointTo
          (list axis point)))
      (if (not (vl-catch-all-error-p closest))
        (progn
          (setq station
            (vl-catch-all-apply
              'vlax-curve-getDistAtPoint
              (list axis closest)))
          (if (not (vl-catch-all-error-p station))
            (progn
              (setq param
                (vl-catch-all-apply 'vlax-curve-getParamAtDist (list axis station)))
              (if (vl-catch-all-error-p param)
                (setq param (vl-catch-all-apply 'vlax-curve-getParamAtPoint (list axis closest))))
              (if (not (vl-catch-all-error-p param))
                (setq der (vl-catch-all-apply 'vlax-curve-getFirstDeriv (list axis param))))
              (setq distance (qhpe26:distance-2d point closest))
              (if (and (not (vl-catch-all-error-p der)) der)
                (progn
                  (setq vx (- (car point) (car closest))
                        vy (- (cadr point) (cadr closest))
                        cross (- (* (car der) vy) (* (cadr der) vx))
                        signed-offset (if (>= cross 0.0) distance (- distance))))
                (setq signed-offset distance))
              (list station signed-offset))))))))

(defun qhpe31:side-limit (signed-offset left-width / right-width)
  (setq right-width
    (if (and (numberp qhpe:*right-width-current*) (> qhpe:*right-width-current* 0.0))
      qhpe:*right-width-current*
      left-width))
  (if (and (numberp signed-offset) (< signed-offset 0.0)) right-width left-width)
)

(defun qhpe26:info-inside-p (info search-width total / station offset limit tol)
  (if (and (listp info) (numberp (car info)) (numberp (cadr info)))
    (progn
      (setq station (car info)
            offset (cadr info)
            limit (qhpe31:side-limit offset search-width)
            tol 1.0e-7)
      (and (>= station (- tol))
           (<= station (+ total tol))
           (<= (abs offset) (+ limit tol))))))

(defun qhpe26:info-range-index (info ranges search-width total)
  (if (qhpe26:info-inside-p info search-width total)
    (qhpe23:range-index ranges (qhpe:clamp (car info) 0.0 total))))

(defun qhpe26:valid-info-stations (infos / result info)
  (foreach info infos
    (if (and (listp info) (numberp (car info)))
      (setq result (cons (car info) result))))
  (reverse result))

(defun qhpe26:add-curb-leaf
  (stats intervals target kind piece-length infos total / stations lo hi center eps)
  (if (and (numberp target) (> piece-length 1.0e-10))
    (progn
      (setq stats (qhpe23:add-curb-stat stats target kind piece-length)
            stations (qhpe26:valid-info-stations infos))
      (if stations
        (progn
          (setq lo (qhpe:clamp (apply 'min stations) 0.0 total)
                hi (qhpe:clamp (apply 'max stations) 0.0 total))
          (if (<= (- hi lo) 1.0e-7)
            (progn
              (setq center (/ (+ lo hi) 2.0)
                    eps (min 0.01 (max 0.001 (/ piece-length 2.0)))
                    lo (max 0.0 (- center eps))
                    hi (min total (+ center eps)))))
          (if (> (- hi lo) 1.0e-9)
            (setq intervals (cons (list lo hi) intervals)))))))
  (list stats intervals))

;;; Subdivisao adaptativa. Um trecho inteiro e aceito quando inicio, meio e fim
;;; estao dentro da largura de busca e pertencem a mesma estaca. Nos demais
;;; casos ele e dividido apenas ate resolver a divisa, sem amostrar a cada
;;; 0,20 m como antes.
(defun qhpe26:allocate-curb-piece
  (axis total ent kind a b ranges station-step search-width stats intervals depth
    / midpoint piece-length p0 pm p1 i0 im i1 in0 inm in1 all-in same-range
      safe-outside limit result-left result-right target)
  (setq midpoint (/ (+ a b) 2.0)
        piece-length (- b a)
        p0 (qhpe26:project-info axis (qhpe:safe-curve-point-at-dist ent a))
        pm (qhpe26:project-info axis (qhpe:safe-curve-point-at-dist ent midpoint))
        p1 (qhpe26:project-info axis (qhpe:safe-curve-point-at-dist ent b))
        in0 (qhpe26:info-inside-p p0 search-width total)
        inm (qhpe26:info-inside-p pm search-width total)
        in1 (qhpe26:info-inside-p p1 search-width total)
        i0 (if in0 (qhpe23:range-index ranges (qhpe:clamp (car p0) 0.0 total)))
        im (if inm (qhpe23:range-index ranges (qhpe:clamp (car pm) 0.0 total)))
        i1 (if in1 (qhpe23:range-index ranges (qhpe:clamp (car p1) 0.0 total)))
        all-in (and in0 inm in1)
        same-range (and (numberp i0) (numberp im) (numberp i1)
                        (= i0 im) (= im i1))
        limit (max 0.02 (min 0.10 (/ station-step 400.0)))
        safe-outside
          (and (not inm)
               (listp pm)
               (numberp (cadr pm))
               (> (abs (cadr pm))
                  (+ (qhpe31:side-limit (cadr pm) search-width)
                     (/ piece-length 2.0) 1.0e-7))))

  (cond
    ((<= piece-length 1.0e-10)
      (list stats intervals))

    ;; Parcela integralmente localizada dentro de uma unica estaca.
    ((and all-in same-range)
      (qhpe26:add-curb-leaf
        stats intervals im kind piece-length (list p0 pm p1) total))

    ;; Pela desigualdade triangular, nem mesmo o ponto mais proximo da
    ;; parcela pode atingir a largura de busca.
    (safe-outside
      (list stats intervals))

    ;; Limite fino: decide pelo ponto medio.
    ((or (>= depth 18) (<= piece-length limit))
      (if inm
        (qhpe26:add-curb-leaf
          stats intervals im kind piece-length (list p0 pm p1) total)
        (list stats intervals)))

    (T
      (setq result-left
        (qhpe26:allocate-curb-piece
          axis total ent kind a midpoint ranges station-step search-width
          stats intervals (1+ depth))
            result-right
        (qhpe26:allocate-curb-piece
          axis total ent kind midpoint b ranges station-step search-width
          (car result-left) (cadr result-left) (1+ depth)))
      result-right)))

;;; Nova analise protegida: uma falha isolada em uma polilinha de meio-fio
;;; nao interrompe QHPED ou QHPEA. A largura de busca e aplicada aos dois.
(defun qhpe23:analyze-curbs
  (axis total selection-set ranges station-step search-width
    / counter ent segments-result segments segment kind d0 d1 result
      stats straight-intervals curved-intervals interval-list index item rows
      straight curved total-straight total-curved skipped)
  (setq counter 0
        stats nil
        straight-intervals nil
        curved-intervals nil
        skipped 0)

  (if selection-set
    (repeat (sslength selection-set)
      (setq ent (ssname selection-set counter))
      (if (qhpe26:valid-curb-source-p ent)
        (progn
          (setq segments-result
            (vl-catch-all-apply 'qhpe23:entity-segments (list ent)))
          (if (vl-catch-all-error-p segments-result)
            (setq skipped (1+ skipped))
            (progn
              (setq segments segments-result)
              (foreach segment segments
                (if (and (listp segment)
                         (member (car segment) '(RETO CURVO))
                         (numberp (cadr segment))
                         (numberp (caddr segment))
                         (> (caddr segment) (+ (cadr segment) 1.0e-10)))
                  (progn
                    (setq kind (car segment)
                          d0 (cadr segment)
                          d1 (caddr segment)
                          interval-list
                            (if (= kind 'CURVO)
                              curved-intervals
                              straight-intervals)
                          result
                            (vl-catch-all-apply
                              'qhpe26:allocate-curb-piece
                              (list axis total ent kind d0 d1 ranges
                                station-step search-width stats interval-list 0)))
                    (if (vl-catch-all-error-p result)
                      (setq skipped (1+ skipped))
                      (progn
                        (setq stats (car result)
                              interval-list (cadr result))
                        (if (= kind 'CURVO)
                          (setq curved-intervals interval-list)
                          (setq straight-intervals interval-list)))))))))))
        (setq skipped (1+ skipped)))
      (setq counter (1+ counter)))

  (setq index 0
        total-straight 0.0
        total-curved 0.0)
  (foreach interval ranges
    (setq item (assoc index stats)
          straight (if item (cadr item) 0.0)
          curved (if item (caddr item) 0.0)
          total-straight (+ total-straight straight)
          total-curved (+ total-curved curved)
          rows (cons (list (car interval) (cadr interval) straight curved) rows)
          index (1+ index)))

  (if (> skipped 0)
    (princ
      (strcat "\nAviso: " (itoa skipped)
        " segmento(s)/objeto(s) de meio-fio invalidos foram ignorados.")))

  (list
    (reverse rows)
    (qhpe:merge-intervals straight-intervals 0.02)
    (qhpe:merge-intervals curved-intervals 0.02)
    total-straight
    total-curved))

;;; Recalculo final usando a largura de busca tambem para os meios-fios.
(defun qhpe25:compute-data
  (axis hatch-ss curb-layer station-step start-station search-width linear-factor
    space / total counter hatch hatch-records records layers projected-result
    projected diagram-data area-rows ranges curb-ss curb-analysis curb-rows
    curb-straight curb-curved curb-total-straight curb-total-curved)
  (setq total (qhpe:curve-length axis)
        counter 0
        records nil)
  (princ "\nGerando limites e verificando ilhas das hachuras...")
  (repeat (sslength hatch-ss)
    (setq hatch (ssname hatch-ss counter)
          hatch-records (qhpe:make-hatch-records hatch space))
    (if hatch-records (setq records (append hatch-records records)))
    (setq counter (1+ counter)))
  (if (null records)
    nil
    (progn
      (setq layers (qhpe:unique-layers records))
      (princ "\nCalculando areas por estaca...")
      (setq area-rows
        (qhpe:process-intervals axis total records layers start-station
          station-step search-width linear-factor space))
      (setq layers (qhpe:sort-layers-by-total layers area-rows))
      (princ "\nCalculando inicios e finais dos pavimentos...")
      (setq projected-result
        (vl-catch-all-apply 'qhpe:projection-intervals-by-layer
          (list axis total records station-step)))
      (if (vl-catch-all-error-p projected-result)
        (setq projected nil)
        (setq projected projected-result))
      (setq diagram-data (qhpe2:merged-projections layers projected))
      (setq ranges (qhpe23:station-ranges total start-station station-step)
            curb-ss (qhpe23:curb-selection-set curb-layer))
      (if curb-ss
        (progn
          (qhpe24:curb-output-layers curb-layer)
          (princ
            (strcat "\nCalculando meios-fios dentro da largura de busca de "
              (rtos search-width 2 2) " unidades..."))
          (setq curb-analysis
            (qhpe23:analyze-curbs
              axis total curb-ss ranges station-step search-width)
                curb-rows (nth 0 curb-analysis)
                curb-straight (nth 1 curb-analysis)
                curb-curved (nth 2 curb-analysis)
                curb-total-straight (nth 3 curb-analysis)
                curb-total-curved (nth 4 curb-analysis)))
        (setq curb-rows nil curb-straight nil curb-curved nil
              curb-total-straight 0.0 curb-total-curved 0.0))
      (list
        (cons 'TOTAL total)
        (cons 'LAYERS layers)
        (cons 'DIAGRAM-DATA diagram-data)
        (cons 'AREA-ROWS area-rows)
        (cons 'CURB-ROWS curb-rows)
        (cons 'CURB-STRAIGHT curb-straight)
        (cons 'CURB-CURVED curb-curved)
        (cons 'CURB-TOTAL-STRAIGHT curb-total-straight)
        (cons 'CURB-TOTAL-CURVED curb-total-curved)
        (cons 'CURB-SOURCE-LAYER curb-layer)
        (cons 'HAS-CURB (if curb-ss T nil))))))

;;; ================================================================
;;; QHPE - ESTILO E ESCALA DAS TABELAS
;;;
;;; - Estilo de tabela exclusivo: QHPE_TABELA;
;;; - Estilo de texto exclusivo: QHPE_ROMANS (romans.shx);
;;; - QHPEESTILOTABELAS aplica o estilo nas tabelas existentes;
;;; - QHPEESCALATABELAS altera a escala sem deslocar o centro de cada tabela.
;;; ================================================================

(setq qhpe:*version* "ATUAL")
(setq qhpe27:*table-style-name* "QHPE_TABELA")
(setq qhpe27:*table-text-style-name* "QHPE_ROMANS")

(defun qhpe27:safe-call (fn args / result)
  (setq result (vl-catch-all-apply fn args))
  (if (vl-catch-all-error-p result) nil result)
)

(defun qhpe27:ensure-table-style
  (/ acad doc dictionaries table-dictionary result table-style text-style
     default-height)
  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        text-style (qhpe21:ensure-romans-style)
        default-height
          (max 0.01
            (qhpe25:getenv-real "TEXT"
              (if qhpe:*diagram-text-height-default*
                qhpe:*diagram-text-height-default*
                0.50))))

  (setq dictionaries (vla-get-Dictionaries doc)
        table-dictionary
          (qhpe27:safe-call 'vla-Item
            (list dictionaries "ACAD_TABLESTYLE")))

  (if table-dictionary
    (progn
      (setq result
        (qhpe27:safe-call 'vla-Item
          (list table-dictionary qhpe27:*table-style-name*)))
      (if result
        (setq table-style result)
        (setq table-style
          (qhpe27:safe-call 'vla-AddObject
            (list table-dictionary qhpe27:*table-style-name*
              "AcDbTableStyle"))))

      (if table-style
        (progn
          (qhpe27:safe-call 'vla-put-Description
            (list table-style "Tabelas de quantitativos do QHPE"))
          (qhpe27:safe-call 'vla-put-HorzCellMargin
            (list table-style (* 0.55 default-height)))
          (qhpe27:safe-call 'vla-put-VertCellMargin
            (list table-style (* 0.30 default-height)))

          ;; Define a fonte e a altura padrao para os tres tipos de linha.
          (foreach row-type
            (list qhpe:acTitleRow qhpe:acHeaderRow qhpe:acDataRow)
            (qhpe27:safe-call 'vla-SetTextStyle
              (list table-style row-type text-style))
            (qhpe27:safe-call 'vla-SetTextHeight
              (list table-style row-type default-height)))

          ;; Alinhamentos padrao. As celulas especificas continuam podendo
          ;; receber ajustes proprios durante a criacao das tabelas.
          (qhpe27:safe-call 'vla-SetAlignment
            (list table-style qhpe:acTitleRow qhpe23:acMiddleCenter))
          (qhpe27:safe-call 'vla-SetAlignment
            (list table-style qhpe:acHeaderRow qhpe23:acMiddleCenter))
          (qhpe27:safe-call 'vla-SetAlignment
            (list table-style qhpe:acDataRow qhpe23:acMiddleLeft))))))

  (if table-style
    qhpe27:*table-style-name*
    (getvar "CTABLESTYLE"))
)

(defun qhpe27:apply-table-style (table / style-name ent)
  (if table
    (progn
      (setq style-name (qhpe27:ensure-table-style))
      (if style-name
        (qhpe27:safe-call 'vla-put-StyleName (list table style-name)))
      ;; Garante que todos os textos usem o estilo Romans exclusivo.
      (foreach row-type
        (list qhpe:acTitleRow qhpe:acHeaderRow qhpe:acDataRow)
        (qhpe27:safe-call 'vla-SetTextStyle
          (list table row-type qhpe27:*table-text-style-name*)))
      (setq ent (vlax-vla-object->ename table))
      (if ent (qhpe26:tag-output ent "TABELA_QHPE"))))
  table
)

(defun qhpe27:table-bbox-center (table / mn mx result pmin pmax)
  (setq result
    (vl-catch-all-apply 'vla-GetBoundingBox (list table 'mn 'mx)))
  (if (not (vl-catch-all-error-p result))
    (progn
      (setq pmin (qhpe:array->list mn)
            pmax (qhpe:array->list mx))
      (list
        (/ (+ (car pmin) (car pmax)) 2.0)
        (/ (+ (cadr pmin) (cadr pmax)) 2.0)
        (/ (+ (if (caddr pmin) (caddr pmin) 0.0)
              (if (caddr pmax) (caddr pmax) 0.0)) 2.0)))))

(defun qhpe27:table-selection (scope / filter selection)
  (cond
    ((= scope "Selecionar")
      (princ "\nSelecione as tabelas QHPE que deseja alterar: ")
      (ssget '((0 . "ACAD_TABLE"))))
    ((= scope "PorEstaca")
      (ssget "_X"
        (list '(0 . "ACAD_TABLE")
          (cons 8 qhpe:*table-layer-name*)
          (cons 410 (getvar "CTAB")))))
    ((= scope "Resumo")
      (ssget "_X"
        (list '(0 . "ACAD_TABLE")
          (cons 8 qhpe:*summary-layer-name*)
          (cons 410 (getvar "CTAB")))))
    (T
      (ssget "_X"
        (list '(0 . "ACAD_TABLE")
          (cons 8
            (strcat qhpe:*table-layer-name* "," qhpe:*summary-layer-name*))
          (cons 410 (getvar "CTAB"))))))
)

(defun qhpe27:prompt-table-scope (/ scope)
  (initget "Todas PorEstaca Resumo Selecionar")
  (setq scope
    (getkword
      "\nTabelas a alterar [Todas/PorEstaca/Resumo/Selecionar] <Todas>: "))
  (if scope scope "Todas")
)

(defun qhpe27:apply-style-to-selection (selection / index ent table count)
  (setq index 0 count 0)
  (if selection
    (repeat (sslength selection)
      (setq ent (ssname selection index)
            table (qhpe:safe-vla-object ent))
      (if (and table (= (vla-get-ObjectName table) "AcDbTable"))
        (progn
          (qhpe27:apply-table-style table)
          (qhpe27:safe-call 'vla-RecomputeTableBlock (list table :vlax-true))
          (qhpe27:safe-call 'vla-Update (list table))
          (setq count (1+ count))))
      (setq index (1+ index))))
  count
)

(defun c:QHPEESTILOTABELAS
  (/ acad doc scope selection count)
  (vl-load-com)
  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        scope (qhpe27:prompt-table-scope)
        selection (qhpe27:table-selection scope))
  (if selection
    (progn
      (vla-StartUndoMark doc)
      (setq count (qhpe27:apply-style-to-selection selection))
      (vla-EndUndoMark doc)
      (vla-Regen doc 1)
      (princ
        (strcat "\nEstilo " qhpe27:*table-style-name* " aplicado em "
          (itoa count) " tabela(s).")))
    (princ "\nNenhuma tabela QHPE encontrada."))
  (princ)
)

(defun c:QHPEESCALATABELAS
  (/ acad doc scope selection factor index ent table center result count)
  (vl-load-com)
  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        scope (qhpe27:prompt-table-scope)
        selection (qhpe27:table-selection scope))
  (if (null selection)
    (princ "\nNenhuma tabela QHPE encontrada.")
    (progn
      (initget 6)
      (setq factor
        (getreal
          "\nFator de escala relativo (ex.: 1.20 aumenta 20%, 0.80 reduz 20%) <1.00>: "))
      (if (null factor) (setq factor 1.0))
      (if (= factor 1.0)
        (princ "\nEscala mantida em 1.00; nenhuma alteracao geometrica realizada.")
        (progn
          (vla-StartUndoMark doc)
          (setq index 0 count 0)
          (repeat (sslength selection)
            (setq ent (ssname selection index)
                  table (qhpe:safe-vla-object ent))
            (if (and table (= (vla-get-ObjectName table) "AcDbTable"))
              (progn
                (qhpe27:apply-table-style table)
                ;; O centro geometrico e usado como base. Assim, cada tabela
                ;; aumenta ou diminui sem sair do lugar visual onde foi posta.
                (setq center (qhpe27:table-bbox-center table))
                (if center
                  (progn
                    (setq result
                      (vl-catch-all-apply 'vla-ScaleEntity
                        (list table (vlax-3d-point center) factor)))
                    (if (not (vl-catch-all-error-p result))
                      (progn
                        (qhpe27:safe-call 'vla-RecomputeTableBlock
                          (list table :vlax-true))
                        (qhpe27:safe-call 'vla-Update (list table))
                        (setq count (1+ count)))))))
            )
            (setq index (1+ index)))
          (vla-EndUndoMark doc)
          (vla-Regen doc 1)
          (princ
            (strcat "\nEscala " (rtos factor 2 3) " aplicada em "
              (itoa count) " tabela(s), mantendo o centro de cada uma.")))))
  )
  (princ)
)

;; Cria os estilos imediatamente ao carregar, sem alterar o estilo corrente.
(qhpe27:ensure-table-style)

(defun qhpe:mostrar-ajuda-comandos ()
  (princ "\n===============================================================")
  (princ "\nQHPE v2.9 - COMANDOS DISPONIVEIS")
  (princ "\n===============================================================")
  (princ "\nQHPEGERAR         - Gera o conjunto completo: diagrama, tabelas por estaca, resumo e exportacao opcional.")
  (princ "\nQHPED              - Gera somente o diagrama linear dos pavimentos e meios-fios, com largura de busca.")
  (princ "\nQHPEA              - Calcula e insere somente as tabelas por estaca, incluindo meios-fios.")
  (princ "\nQHPERESUMO         - Insere a legenda e a tabela geral de totais dos servicos.")
  (princ "\nQHPEUPDATE         - Atualiza um conjunto existente criado pelo QHPEGERAR.")
  (princ "\nQHPEEXPORT         - Exporta novamente os quantitativos formatados para o Excel.")
  (princ "\nQHPEESTILOTABELAS  - Aplica os estilos QHPE_TABELA e QHPE_ROMANS nas tabelas existentes.")
  (princ "\nQHPEESCALATABELAS  - Aumenta ou reduz as tabelas sem alterar o centro de cada uma.")
  (princ "\nQHPE               - Executa a quantificacao tradicional com saida em tabela e/ou CSV.")
  (princ "\nQHPEAJUDA          - Exibe novamente esta lista de comandos e descricoes.")
  (princ "\n===============================================================")
  (princ)
)

(defun c:QHPEAJUDA ()
  (qhpe:mostrar-ajuda-comandos)
)



;;; QHPE v3.2 - larguras independentes por lado
(if (not (and (numberp qhpe:*right-width-default*) (> qhpe:*right-width-default* 0.0)))
  (setq qhpe:*right-width-default* qhpe:*width-default*))
(if (not (and (numberp qhpe:*right-width-current*) (> qhpe:*right-width-current* 0.0)))
  (setq qhpe:*right-width-current* qhpe:*right-width-default*))

;;; ================================================================
;;; QHPE v3.0 - CORRECOES DE MEIO-FIO E DESEMPENHO
;;; ================================================================

(setq qhpe:*version* "3.0")
(setq qhpe30:*table-style-cache* nil)
(setq qhpe30:*pending-tables* nil)
(setq qhpe30:*space-cache* nil)

;;; Aceita tambem POLYLINE 3D. Objetos produzidos pelo proprio QHPE
;;; continuam excluidos pela marcacao XData. Nao usa mais a heuristica de
;;; "retangulo com quatro vertices", pois ela podia eliminar meios-fios reais.
(defun qhpe26:valid-curb-source-p (ent / data typ length)
  (if (and ent (setq data (entget ent)))
    (progn
      (setq typ (cdr (assoc 0 data))
            length (qhpe:curve-length ent))
      (and
        (member typ '("LWPOLYLINE" "POLYLINE" "LINE" "ARC"))
        (not (qhpe26:output-entity-p ent))
        (numberp length)
        (> length 1.0e-8)))
    nil)
)

;;; Selecao protegida, incluindo polilinhas 2D e 3D do layer informado.
(defun qhpe23:curb-selection-set (layer / filter raw result index ent)
  (if layer
    (progn
      (setq filter
        (list
          (cons 0 "LWPOLYLINE,POLYLINE,LINE,ARC")
          (cons 8 layer)
          (cons 410 (getvar "CTAB"))))
      (setq raw (ssget "_X" filter)
            result (ssadd)
            index 0)
      (if raw
        (repeat (sslength raw)
          (setq ent (ssname raw index))
          (if (qhpe26:valid-curb-source-p ent)
            (ssadd ent result))
          (setq index (1+ index))))
      (if (> (sslength result) 0) result nil)))
)

(defun qhpe30:interval-overlaps-p (range intervals / found item)
  (setq found nil)
  (while (and intervals (not found))
    (setq item (car intervals))
    (if (and (listp item)
             (numberp (car item))
             (numberp (cadr item))
             (> (min (cadr range) (cadr item))
                (+ (max (car range) (car item)) 1.0e-8)))
      (setq found T)
      (setq intervals (cdr intervals))))
  found
)

;;; Garante que toda estaca com comprimento calculado tenha representacao
;;; grafica, mesmo quando uma API de projecao retorna pontos coincidentes.
(defun qhpe30:ensure-curb-intervals
  (ranges rows intervals kind / merged index range row value)
  (setq merged (qhpe:merge-intervals intervals 0.02)
        index 0)
  (foreach range ranges
    (setq row (nth index rows)
          value
            (cond
              ((null row) 0.0)
              ((= kind 'CURVO) (nth 3 row))
              (T (nth 2 row))))
    (if (and (numberp value)
             (> value 1.0e-8)
             (not (qhpe30:interval-overlaps-p range merged)))
      (setq merged (cons (list (car range) (cadr range)) merged)))
    (setq index (1+ index)))
  (qhpe:merge-intervals merged 0.02)
)

;;; Analise final dos meios-fios: robusta para 2D/3D e com garantia de faixa.
(defun qhpe23:analyze-curbs
  (axis total selection-set ranges station-step search-width
    / counter ent segments-result segments segment kind d0 d1 result
      stats straight-intervals curved-intervals interval-list index item rows
      straight curved total-straight total-curved skipped valid-count)
  (setq counter 0
        stats nil
        straight-intervals nil
        curved-intervals nil
        skipped 0
        valid-count 0)

  (if selection-set
    (repeat (sslength selection-set)
      (setq ent (ssname selection-set counter))
      (if (qhpe26:valid-curb-source-p ent)
        (progn
          (setq valid-count (1+ valid-count)
                segments-result
                  (vl-catch-all-apply 'qhpe23:entity-segments (list ent)))
          (if (vl-catch-all-error-p segments-result)
            (setq skipped (1+ skipped))
            (progn
              (setq segments segments-result)
              (foreach segment segments
                (if (and (listp segment)
                         (member (car segment) '(RETO CURVO))
                         (numberp (cadr segment))
                         (numberp (caddr segment))
                         (> (caddr segment) (+ (cadr segment) 1.0e-10)))
                  (progn
                    (setq kind (car segment)
                          d0 (cadr segment)
                          d1 (caddr segment)
                          interval-list
                            (if (= kind 'CURVO)
                              curved-intervals
                              straight-intervals)
                          result
                            (vl-catch-all-apply
                              'qhpe26:allocate-curb-piece
                              (list axis total ent kind d0 d1 ranges
                                station-step search-width stats interval-list 0)))
                    (if (vl-catch-all-error-p result)
                      (setq skipped (1+ skipped))
                      (progn
                        (setq stats (car result)
                              interval-list (cadr result))
                        (if (= kind 'CURVO)
                          (setq curved-intervals interval-list)
                          (setq straight-intervals interval-list))))))))))
        (setq skipped (1+ skipped)))
      (setq counter (1+ counter))))

  (setq index 0
        total-straight 0.0
        total-curved 0.0
        rows nil)
  (foreach interval ranges
    (setq item (assoc index stats)
          straight (if item (cadr item) 0.0)
          curved (if item (caddr item) 0.0)
          total-straight (+ total-straight straight)
          total-curved (+ total-curved curved)
          rows (cons (list (car interval) (cadr interval) straight curved) rows)
          index (1+ index)))
  (setq rows (reverse rows)
        straight-intervals
          (qhpe30:ensure-curb-intervals
            ranges rows straight-intervals 'RETO)
        curved-intervals
          (qhpe30:ensure-curb-intervals
            ranges rows curved-intervals 'CURVO))

  (princ
    (strcat "\nMeios-fios considerados: " (itoa valid-count)
      " objeto(s); reto=" (rtos total-straight 2 2)
      "; curvo=" (rtos total-curved 2 2) "."))
  (if (> skipped 0)
    (princ
      (strcat "\nAviso: " (itoa skipped)
        " segmento(s)/objeto(s) de meio-fio foram ignorados.")))

  (list rows straight-intervals curved-intervals
    total-straight total-curved)
)

;;; Medicao de texto sem criar entidades temporarias. Isso elimina centenas
;;; de criacoes/apagamentos de TEXT durante a montagem das tabelas.
(defun qhpe24:safe-text-width (text height style / chars)
  (setq chars (float (qhpe24:visible-text-length text)))
  (* chars height 0.80)
)

;;; O estilo e criado uma vez por sessao e apenas aplicado por nome.
(defun qhpe27:apply-table-style (table / style-name ent)
  (if table
    (progn
      (if (null qhpe30:*table-style-cache*)
        (setq qhpe30:*table-style-cache* (qhpe27:ensure-table-style)))
      (setq style-name qhpe30:*table-style-cache*)
      (if style-name
        (qhpe27:safe-call 'vla-put-StyleName (list table style-name)))
      (setq ent (vlax-vla-object->ename table))
      (if ent (qhpe26:tag-output ent "TABELA_QHPE"))))
  table
)

;;; Criacao otimizada: sem recomputar o bloco a cada celula/tabela e sem
;;; repetir configuracoes que ja pertencem ao estilo QHPE_TABELA.
(defun qhpe23:create-native-table
  (mid-x available-width bottom title rows preferred-height layer style
    / acad doc space fit height w1 w2 w3 total-width margin-x margin-y
      title-height row-height row-count table-height top-left table
      data-row row)

  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (if qhpe30:*space-cache*
                qhpe30:*space-cache*
                (vla-get-ModelSpace doc))
        fit (qhpe23:fit-table title rows preferred-height available-width style)
        height (nth 0 fit)
        w1 (nth 1 fit)
        w2 (nth 2 fit)
        w3 (nth 3 fit)
        total-width (+ w1 w2 w3)
        margin-x (* 0.55 height)
        margin-y (* 0.30 height)
        title-height (+ (* 1.28 height) (* 2.0 margin-y))
        row-height (+ (* 1.16 height) (* 2.0 margin-y))
        row-count (+ 2 (length rows))
        table-height (+ title-height (* (1- row-count) row-height))
        top-left
          (list (- mid-x (/ total-width 2.0)) (+ bottom table-height) 0.0)
        table
          (vla-AddTable
            space (vlax-3d-point top-left) row-count 3 row-height w1))

  (if table
    (progn
      (qhpe23:safe-table-call
        'vla-put-RegenerateTableSuppressed (list table :vlax-true))
      (qhpe23:safe-table-call 'vla-put-Layer (list table layer))
      (qhpe27:apply-table-style table)
      (qhpe23:safe-table-call 'vla-put-HorzCellMargin (list table margin-x))
      (qhpe23:safe-table-call 'vla-put-VertCellMargin (list table margin-y))
      (qhpe23:safe-table-call 'vla-put-TitleSuppressed (list table :vlax-false))
      (qhpe23:safe-table-call 'vla-put-HeaderSuppressed (list table :vlax-false))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 0 w1))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 1 w2))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 2 w3))
      (qhpe23:safe-table-call 'vla-SetRowHeight (list table 0 title-height))
      (qhpe23:safe-table-call 'vla-MergeCells (list table 0 0 0 2))
      (qhpe23:safe-table-call
        'vla-SetTextHeight
        (list table (+ qhpe:acTitleRow qhpe:acHeaderRow qhpe:acDataRow) height))

      (qhpe:set-table-text table 0 0 title)
      (qhpe:set-table-text table 1 0 "SERVI\\U+00C7O")
      (qhpe:set-table-text table 1 1 "QUANTIDADE")
      (qhpe:set-table-text table 1 2 "ACUMULADO")
      (qhpe23:safe-table-call
        'vla-SetCellAlignment (list table 0 0 qhpe23:acMiddleCenter))
      (qhpe23:safe-table-call
        'vla-SetCellAlignment (list table 1 0 qhpe23:acMiddleCenter))
      (qhpe23:safe-table-call
        'vla-SetCellAlignment (list table 1 1 qhpe23:acMiddleCenter))
      (qhpe23:safe-table-call
        'vla-SetCellAlignment (list table 1 2 qhpe23:acMiddleCenter))

      (setq data-row 2)
      (foreach row rows
        (qhpe:set-table-text table data-row 0 (nth 0 row))
        (qhpe:set-table-text table data-row 1 (nth 1 row))
        (qhpe:set-table-text table data-row 2 (nth 2 row))
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table data-row 0 qhpe23:acMiddleLeft))
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table data-row 1 qhpe23:acMiddleRight))
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table data-row 2 qhpe23:acMiddleRight))
        (setq data-row (1+ data-row)))

      ;; Mantem a regeneracao suspensa ate todas as tabelas serem preenchidas.
      (setq qhpe30:*pending-tables* (cons table qhpe30:*pending-tables*))))
  table
)

(defun qhpe30:release-pending-tables (/ table)
  (foreach table qhpe30:*pending-tables*
    (qhpe23:safe-table-call
      'vla-put-RegenerateTableSuppressed (list table :vlax-false)))
  (setq qhpe30:*pending-tables* nil)
  T
)

;;; Desenho otimizado das tabelas, com uma unica etapa final de regeneracao.
(defun qhpe23:draw-native-tables
  (insertion area-rows layers curb-rows start-station station-step linear-factor
    band-height text-height annotation-layer include-curb
    / acad doc x0 y0 geometry band-top area-accum curb-accum-straight
      curb-accum-curved index area-row curb-row layer current-area d0 d1 mid-x
      available-width table-bottom table-data title data-rows style total-count)

  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        qhpe30:*space-cache* (vla-get-ModelSpace doc)
        qhpe30:*pending-tables* nil
        x0 (car insertion)
        y0 (cadr insertion)
        geometry
          (qhpe23:diagram-geometry
            insertion station-step (length layers) band-height text-height
            include-curb)
        band-top (nth 3 geometry)
        area-accum nil
        curb-accum-straight 0.0
        curb-accum-curved 0.0
        index 0
        total-count (length area-rows)
        style (qhpe21:ensure-romans-style))

  (princ
    (strcat "\nInserindo " (itoa total-count)
      " objetos TABLE em modo otimizado..."))
  (foreach area-row area-rows
    (foreach layer layers
      (setq current-area
        (max 0.0 (qhpe:row-area (nth 3 area-row) layer))
            area-accum
              (qhpe:update-accumulated area-accum layer current-area)))
    (setq curb-row (if include-curb (nth index curb-rows) nil))
    (if include-curb
      (setq curb-accum-straight
              (+ curb-accum-straight (qhpe23:curb-row-value curb-row 'RETO))
            curb-accum-curved
              (+ curb-accum-curved (qhpe23:curb-row-value curb-row 'CURVO))))

    (setq d0 (nth 4 area-row)
          d1 (nth 5 area-row))
    (if (and (numberp d0) (numberp d1) (> d1 d0))
      (progn
        (setq mid-x (+ x0 (/ (+ d0 d1) 2.0))
              available-width
                (max (* 2.0 text-height)
                     (- d1 d0 (* 0.35 text-height)))
              table-bottom (qhpe31:station-top-y insertion)
              table-bottom (if (numberp table-bottom) table-bottom band-top)
              table-data
                (qhpe23:table-data
                  area-row layers area-accum curb-row
                  curb-accum-straight curb-accum-curved linear-factor
                  start-station station-step include-curb)
              title (car table-data)
              data-rows (cadr table-data))
        (qhpe23:create-native-table
          mid-x available-width table-bottom title data-rows
          text-height annotation-layer style)))
    (setq index (1+ index))
    (if (= 0 (rem index 10))
      (princ (strcat "\rTabelas: " (itoa index) "/" (itoa total-count)))))

  (qhpe30:release-pending-tables)
  (setq qhpe30:*space-cache* nil)
  (princ (strcat "\rTabelas: " (itoa total-count) "/" (itoa total-count) "."))
  T
)

(defun qhpe30:table-center-fast (table / insertion width height rotation x y z dx dy)
  (setq insertion (qhpe27:safe-call 'vla-get-InsertionPoint (list table))
        width (qhpe27:safe-call 'vla-get-Width (list table))
        height (qhpe27:safe-call 'vla-get-Height (list table))
        rotation (qhpe27:safe-call 'vla-get-Rotation (list table)))
  (if (and insertion (numberp width) (numberp height))
    (progn
      (setq insertion (qhpe:array->list insertion)
            rotation (if (numberp rotation) rotation 0.0)
            x (car insertion)
            y (cadr insertion)
            z (if (caddr insertion) (caddr insertion) 0.0)
            dx (/ width 2.0)
            dy (- (/ height 2.0)))
      (list
        (+ x (- (* dx (cos rotation)) (* dy (sin rotation))))
        (+ y (+ (* dx (sin rotation)) (* dy (cos rotation))))
        z))
    (qhpe27:table-bbox-center table))
)

;;; Aplicacao de estilo sem recomputar cada tabela individualmente.
(defun qhpe27:apply-style-to-selection (selection / index ent table count)
  (setq index 0 count 0)
  (if selection
    (repeat (sslength selection)
      (setq ent (ssname selection index)
            table (qhpe:safe-vla-object ent))
      (if (and table (= (vla-get-ObjectName table) "AcDbTable"))
        (progn
          (qhpe27:apply-table-style table)
          (setq count (1+ count))))
      (setq index (1+ index))))
  count
)

;;; Escala rapida: somente ScaleEntity por tabela e uma unica regeneracao.
(defun c:QHPEESCALATABELAS
  (/ acad doc scope selection factor index ent table center result count
    old-regenmode old-cmdecho)
  (vl-load-com)
  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        scope (qhpe27:prompt-table-scope)
        selection (qhpe27:table-selection scope))
  (if (null selection)
    (princ "\nNenhuma tabela QHPE encontrada.")
    (progn
      (initget 6)
      (setq factor
        (getreal
          "\nFator de escala relativo (1.20 aumenta 20%; 0.80 reduz 20%) <1.00>: "))
      (if (null factor) (setq factor 1.0))
      (if (= factor 1.0)
        (princ "\nEscala mantida em 1.00.")
        (progn
          (setq old-regenmode (getvar "REGENMODE")
                old-cmdecho (getvar "CMDECHO"))
          (setvar "REGENMODE" 0)
          (setvar "CMDECHO" 0)
          (vla-StartUndoMark doc)
          (setq index 0 count 0)
          (repeat (sslength selection)
            (setq ent (ssname selection index)
                  table (qhpe:safe-vla-object ent))
            (if (and table (= (vla-get-ObjectName table) "AcDbTable"))
              (progn
                (setq center (qhpe30:table-center-fast table))
                (if center
                  (progn
                    (setq result
                      (vl-catch-all-apply 'vla-ScaleEntity
                        (list table (vlax-3d-point center) factor)))
                    (if (not (vl-catch-all-error-p result))
                      (setq count (1+ count)))))))
            (setq index (1+ index))
            (if (= 0 (rem index 20))
              (princ
                (strcat "\rEscalando tabelas: " (itoa index) "/"
                  (itoa (sslength selection))))))
          (vla-EndUndoMark doc)
          (setvar "REGENMODE" old-regenmode)
          (setvar "CMDECHO" old-cmdecho)
          (vla-Regen doc 1)
          (princ
            (strcat "\rEscala " (rtos factor 2 3) " aplicada em "
              (itoa count) " tabela(s), mantendo o centro."))))))
  (princ)
)

(defun qhpe:mostrar-ajuda-comandos ()
  (princ "\n===============================================================")
  (princ "\nQHPE v3.0 - COMANDOS DISPONIVEIS")
  (princ "\n===============================================================")
  (princ "\nQHPEGERAR         - Gera o conjunto completo: diagrama, tabelas, resumo e exportacao opcional.")
  (princ "\nQHPED              - Gera somente o diagrama linear dos pavimentos e meios-fios.")
  (princ "\nQHPEA              - Calcula e insere somente as tabelas por estaca.")
  (princ "\nQHPERESUMO         - Insere a legenda e a tabela geral de totais.")
  (princ "\nQHPEUPDATE         - Atualiza um conjunto existente gerado pelo QHPEGERAR.")
  (princ "\nQHPEEXPORT         - Exporta os quantitativos formatados para o Excel.")
  (princ "\nQHPEESTILOTABELAS  - Aplica o estilo QHPE_TABELA nas tabelas existentes.")
  (princ "\nQHPEESCALATABELAS  - Escala todas as tabelas rapidamente sem mudar o centro.")
  (princ "\nQHPE               - Quantificacao tradicional com saida em tabela e/ou CSV.")
  (princ "\nQHPEAJUDA          - Exibe novamente esta lista.")
  (princ "\n===============================================================")
  (princ)
)



;;; ================================================================
;;; QHPE v3.1 - CORRECAO DA ALTURA EXCESSIVA DAS LINHAS DAS TABELAS
;;; ================================================================
;;;
;;; A otimizacao da v3.0 deixava somente a linha de titulo com altura
;;; explicita. Ao liberar a regeneracao, alguns estilos de TABLE do AutoCAD
;;; reaplicavam alturas padrao muito grandes nas linhas de cabecalho e dados.
;;; Esta versao fixa explicitamente TODAS as linhas antes da regeneracao.
;;; ================================================================

(setq qhpe:*version* "3.1")
(setq qhpe30:*pending-tables* nil)

(defun qhpe31:safe-number (value default)
  (if (numberp value) value default)
)

(defun qhpe31:get-text-height (table row-type default / result)
  (setq result
    (vl-catch-all-apply 'vla-GetTextHeight (list table row-type)))
  (if (and (not (vl-catch-all-error-p result))
           (numberp result)
           (> result 1.0e-9))
    result
    default)
)

(defun qhpe31:set-table-row-heights
  (table row-count title-height row-height / row-index)
  (if (and table (numberp row-count) (> row-count 0))
    (progn
      (qhpe23:safe-table-call
        'vla-SetRowHeight (list table 0 title-height))
      (setq row-index 1)
      (while (< row-index row-count)
        (qhpe23:safe-table-call
          'vla-SetRowHeight (list table row-index row-height))
        (setq row-index (1+ row-index)))))
  table
)

;;; Substitui a criacao otimizada da v3.0, mantendo o ganho de desempenho,
;;; mas fixa largura das colunas e altura de todas as linhas.
(defun qhpe23:create-native-table
  (mid-x available-width bottom title rows preferred-height layer style
    / acad doc space fit height w1 w2 w3 total-width margin-x margin-y
      title-height row-height row-count table-height top-left table
      data-row row)

  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (if qhpe30:*space-cache*
                qhpe30:*space-cache*
                (vla-get-ModelSpace doc))
        fit (qhpe23:fit-table title rows preferred-height available-width style)
        height (nth 0 fit)
        w1 (nth 1 fit)
        w2 (nth 2 fit)
        w3 (nth 3 fit)
        total-width (+ w1 w2 w3)
        margin-x (* 0.55 height)
        margin-y (* 0.30 height)
        title-height (+ (* 1.28 height) (* 2.0 margin-y))
        row-height (+ (* 1.16 height) (* 2.0 margin-y))
        row-count (+ 2 (length rows))
        table-height (+ title-height (* (1- row-count) row-height))
        top-left
          (list (- mid-x (/ total-width 2.0)) (+ bottom table-height) 0.0)
        table
          (vla-AddTable
            space (vlax-3d-point top-left) row-count 3 row-height w1))

  (if table
    (progn
      ;; Suspende a regeneracao enquanto todas as propriedades sao definidas.
      (qhpe23:safe-table-call
        'vla-put-RegenerateTableSuppressed (list table :vlax-true))
      (qhpe23:safe-table-call 'vla-put-Layer (list table layer))
      (qhpe27:apply-table-style table)
      (qhpe23:safe-table-call 'vla-put-HorzCellMargin (list table margin-x))
      (qhpe23:safe-table-call 'vla-put-VertCellMargin (list table margin-y))
      (qhpe23:safe-table-call 'vla-put-TitleSuppressed (list table :vlax-false))
      (qhpe23:safe-table-call 'vla-put-HeaderSuppressed (list table :vlax-false))

      ;; Define cada tipo de linha separadamente. Evita interpretacao
      ;; inconsistente do bitmask combinado em algumas versoes do AutoCAD.
      (qhpe23:safe-table-call
        'vla-SetTextHeight (list table qhpe:acTitleRow height))
      (qhpe23:safe-table-call
        'vla-SetTextHeight (list table qhpe:acHeaderRow height))
      (qhpe23:safe-table-call
        'vla-SetTextHeight (list table qhpe:acDataRow height))

      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 0 w1))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 1 w2))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 2 w3))
      (qhpe31:set-table-row-heights
        table row-count title-height row-height)

      (qhpe23:safe-table-call 'vla-MergeCells (list table 0 0 0 2))
      (qhpe:set-table-text table 0 0 title)
      (qhpe:set-table-text table 1 0 "SERVI\\U+00C7O")
      (qhpe:set-table-text table 1 1 "QUANTIDADE")
      (qhpe:set-table-text table 1 2 "ACUMULADO")
      (qhpe23:safe-table-call
        'vla-SetCellAlignment (list table 0 0 qhpe23:acMiddleCenter))
      (qhpe23:safe-table-call
        'vla-SetCellAlignment (list table 1 0 qhpe23:acMiddleCenter))
      (qhpe23:safe-table-call
        'vla-SetCellAlignment (list table 1 1 qhpe23:acMiddleCenter))
      (qhpe23:safe-table-call
        'vla-SetCellAlignment (list table 1 2 qhpe23:acMiddleCenter))

      (setq data-row 2)
      (foreach row rows
        (qhpe:set-table-text table data-row 0 (nth 0 row))
        (qhpe:set-table-text table data-row 1 (nth 1 row))
        (qhpe:set-table-text table data-row 2 (nth 2 row))
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table data-row 0 qhpe23:acMiddleLeft))
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table data-row 1 qhpe23:acMiddleRight))
        (qhpe23:safe-table-call
          'vla-SetCellAlignment
          (list table data-row 2 qhpe23:acMiddleRight))
        (setq data-row (1+ data-row)))

      ;; Reaplica dimensoes apos inserir os textos, pois algumas versoes
      ;; tentam autoajustar linhas e colunas com base no conteudo.
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 0 w1))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 1 w2))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 2 w3))
      (qhpe31:set-table-row-heights
        table row-count title-height row-height)

      ;; Mantem a regeneracao suspensa ate a liberacao coletiva.
      (setq qhpe30:*pending-tables*
        (cons
          (list table row-count title-height row-height w1 w2 w3)
          qhpe30:*pending-tables*))))
  table
)

;;; Libera as tabelas coletivamente e reforca as dimensoes uma ultima vez.
;;; O reforco apos remover a supressao evita que o estilo corrente volte a
;;; aplicar alturas grandes. Continua havendo apenas uma REGEN ao final.
(defun qhpe30:release-pending-tables (/ item table row-count title-height
  row-height w1 w2 w3)
  (foreach item qhpe30:*pending-tables*
    (setq table (nth 0 item)
          row-count (nth 1 item)
          title-height (nth 2 item)
          row-height (nth 3 item)
          w1 (nth 4 item)
          w2 (nth 5 item)
          w3 (nth 6 item))
    (if (qhpe:object-alive-p table)
      (progn
        (qhpe23:safe-table-call
          'vla-put-RegenerateTableSuppressed (list table :vlax-false))
        (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 0 w1))
        (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 1 w2))
        (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 2 w3))
        (qhpe31:set-table-row-heights
          table row-count title-height row-height))))
  (setq qhpe30:*pending-tables* nil)
  T
)

;;; Corrige rapidamente as tabelas ja geradas pela v3.0, sem recalcular
;;; areas, hachuras ou meios-fios. Mantem a base inferior de cada tabela.
(defun qhpe31:normalize-existing-table (table / rows data-height title-text-height
  margin-y title-height row-height insertion old-height old-bottom new-height
  new-insertion)
  (if (and table (= (vla-get-ObjectName table) "AcDbTable"))
    (progn
      (setq rows
        (qhpe31:safe-number
          (qhpe27:safe-call 'vla-get-Rows (list table)) 0)
            data-height
              (qhpe31:get-text-height table qhpe:acDataRow 0.30)
            title-text-height
              (qhpe31:get-text-height table qhpe:acTitleRow data-height)
            margin-y
              (qhpe31:safe-number
                (qhpe27:safe-call 'vla-get-VertCellMargin (list table))
                (* 0.30 data-height))
            title-height
              (+ (* 1.28 title-text-height) (* 2.0 margin-y))
            row-height
              (+ (* 1.16 data-height) (* 2.0 margin-y))
            insertion
              (qhpe27:safe-call 'vla-get-InsertionPoint (list table))
            old-height
              (qhpe31:safe-number
                (qhpe27:safe-call 'vla-get-Height (list table)) 0.0))
      (if insertion (setq insertion (qhpe:array->list insertion)))
      (if (and insertion (> old-height 0.0))
        (setq old-bottom (- (cadr insertion) old-height)))

      (qhpe23:safe-table-call
        'vla-put-RegenerateTableSuppressed (list table :vlax-true))
      (qhpe31:set-table-row-heights table rows title-height row-height)
      (qhpe23:safe-table-call
        'vla-put-RegenerateTableSuppressed (list table :vlax-false))
      (qhpe31:set-table-row-heights table rows title-height row-height)

      (if (and insertion old-bottom (> rows 0))
        (progn
          (setq new-height (+ title-height (* (1- rows) row-height))
                new-insertion
                  (list (car insertion) (+ old-bottom new-height)
                    (if (caddr insertion) (caddr insertion) 0.0)))
          (qhpe27:safe-call
            'vla-put-InsertionPoint
            (list table (vlax-3d-point new-insertion)))))
      T)
    nil)
)

(defun c:QHPECORRIGIRTABELAS
  (/ acad doc scope selection index ent table count old-regenmode old-cmdecho)
  (vl-load-com)
  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        scope (qhpe27:prompt-table-scope)
        selection (qhpe27:table-selection scope))
  (if (null selection)
    (princ "\nNenhuma tabela QHPE encontrada.")
    (progn
      (setq old-regenmode (getvar "REGENMODE")
            old-cmdecho (getvar "CMDECHO"))
      (setvar "REGENMODE" 0)
      (setvar "CMDECHO" 0)
      (vla-StartUndoMark doc)
      (setq index 0 count 0)
      (repeat (sslength selection)
        (setq ent (ssname selection index)
              table (qhpe:safe-vla-object ent))
        (if (qhpe31:normalize-existing-table table)
          (setq count (1+ count)))
        (setq index (1+ index)))
      (vla-EndUndoMark doc)
      (setvar "REGENMODE" old-regenmode)
      (setvar "CMDECHO" old-cmdecho)
      (vla-Regen doc 1)
      (princ
        (strcat "\nAltura das linhas corrigida em "
          (itoa count) " tabela(s)."))))
  (princ)
)

(defun qhpe:mostrar-ajuda-comandos ()
  (princ "\n===============================================================")
  (princ "\nQHPE v3.1 - COMANDOS DISPONIVEIS")
  (princ "\n===============================================================")
  (princ "\nQHPEGERAR           - Gera o conjunto completo: diagrama, tabelas, resumo e exportacao opcional.")
  (princ "\nQHPED                - Gera somente o diagrama linear dos pavimentos e meios-fios.")
  (princ "\nQHPEA                - Calcula e insere somente as tabelas por estaca.")
  (princ "\nQHPERESUMO           - Insere a legenda e a tabela geral de totais.")
  (princ "\nQHPEUPDATE           - Atualiza um conjunto existente gerado pelo QHPEGERAR.")
  (princ "\nQHPEEXPORT           - Exporta os quantitativos formatados para o Excel.")
  (princ "\nQHPEESTILOTABELAS    - Aplica o estilo QHPE_TABELA nas tabelas existentes.")
  (princ "\nQHPEESCALATABELAS    - Escala as tabelas sem mudar o centro visual.")
  (princ "\nQHPECORRIGIRTABELAS  - Corrige tabelas da v3.0 com linhas excessivamente altas.")
  (princ "\nQHPE                 - Quantificacao tradicional com saida em tabela e/ou CSV.")
  (princ "\nQHPEAJUDA            - Exibe novamente esta lista.")
  (princ "\n===============================================================")
  (princ)
)



;;; ================================================================
;;; QHPE v3.2 - FORMATACAO, OFFSETS L/R E DIAGNOSTICO DE HATCH
;;; ================================================================
(setq qhpe:*version* "3.3")

(defun qhpe31:make-white-rectangle (x0 y0 x1 y1 layer / ent)
  (setq ent
    (entmakex
      (list
      '(0 . "LWPOLYLINE")
      '(100 . "AcDbEntity")
      (cons 8 layer)
      (cons 62 7)
      '(100 . "AcDbPolyline")
      '(90 . 4)
      '(70 . 1)
      (cons 10 (list x0 y0))
      (cons 10 (list x1 y0))
      (cons 10 (list x1 y1))
      (cons 10 (list x0 y1))
      '(210 0.0 0.0 1.0))))
  (if ent (qhpe26:tag-output ent "FAIXA_DIAGRAMA"))
  ent)

(defun qhpe31:make-pavement-band (x0 x1 y0 y1 layer)
  (if (> (- x1 x0) 1.0e-8)
    (progn
      (qhpe21:make-solid x0 y0 x1 y1 layer)
      (qhpe31:make-white-rectangle x0 y0 x1 y1 layer)
      T)))

(defun qhpe31:ensure-dashed-linetype (/ acad doc ltypes test)
  (if (not (tblsearch "LTYPE" "DASHED"))
    (progn
      (setq acad (vlax-get-acad-object)
            doc (vla-get-ActiveDocument acad)
            ltypes (vla-get-Linetypes doc))
      (setq test (vl-catch-all-apply 'vla-Load (list ltypes "DASHED" "acad.lin")))
      (if (or (vl-catch-all-error-p test) (not (tblsearch "LTYPE" "DASHED")))
        (vl-catch-all-apply 'vla-Load (list ltypes "DASHED" "acadiso.lin")))))
  (if (tblsearch "LTYPE" "DASHED") "DASHED" "CONTINUOUS"))

(defun qhpe31:make-discontinuity-line (p1 p2 layer / ent obj lt)
  (setq lt (qhpe31:ensure-dashed-linetype)
        ent
          (entmakex
            (list
              '(0 . "LINE")
              '(100 . "AcDbEntity")
              (cons 8 layer)
              (cons 6 lt)
              (cons 48 2.0)
              '(100 . "AcDbLine")
              (cons 10 (list (car p1) (cadr p1) 0.0))
              (cons 11 (list (car p2) (cadr p2) 0.0))
              '(210 0.0 0.0 1.0))))
  (if ent
    (progn
      (setq obj (qhpe:safe-vla-object ent))
      (if obj
        (progn
          (vl-catch-all-apply 'vla-put-LinetypeScale (list obj 2.0))
          ;; EntityTransparency recebe string percentual nas versoes atuais.
          (vl-catch-all-apply 'vlax-put-property
            (list obj 'EntityTransparency "50"))))))
  ent)

(defun qhpe31:station-top-y (insertion / x0 y0 ss i ent data p1 p2 tol found top)
  (setq x0 (car insertion)
        y0 (cadr insertion)
        tol 1.0e-5
        ss (ssget "_X"
             (list '(0 . "LINE")
                   (cons 8 qhpe:*diagram-layer-name*)
                   (cons 410 (getvar "CTAB"))))
        i 0
        found nil)
  (if ss
    (repeat (sslength ss)
      (setq ent (ssname ss i)
            data (entget ent)
            p1 (cdr (assoc 10 data))
            p2 (cdr (assoc 11 data)))
      (if (and p1 p2
               (< (abs (- (car p1) x0)) tol)
               (< (abs (- (car p2) x0)) tol)
               (or (< (abs (- (cadr p1) y0)) tol)
                   (< (abs (- (cadr p2) y0)) tol)))
        (progn
          (setq top (max (cadr p1) (cadr p2)))
          (if (or (null found) (> top found)) (setq found top))))
      (setq i (1+ i))))
  found)

(defun c:QHPEDIAGHATCH
  (/ acad doc space old-cmdecho old-osmode ss i ent obj layer handle area-r loops-r records)
  (vl-load-com)
  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        old-cmdecho (getvar "CMDECHO")
        old-osmode (getvar "OSMODE"))
  (setvar "CMDECHO" 0)
  (setvar "OSMODE" 0)
  (qhpe:prepare-temp-layer doc)
  (princ "\nQHPEDIAGHATCH - diagnostico das hachuras selecionadas")
  (setq ss (ssget "_:L" '((0 . "HATCH"))))
  (if ss
    (progn
      (setq i 0)
      (repeat (sslength ss)
        (setq ent (ssname ss i)
              obj (qhpe:safe-vla-object ent)
              layer (cdr (assoc 8 (entget ent)))
              handle (cdr (assoc 5 (entget ent))))
        (princ (strcat "\n--- Handle " handle " | Layer: " layer " ---"))
        (if obj
          (progn
            (setq area-r (vl-catch-all-apply 'vla-get-Area (list obj))
                  loops-r (vl-catch-all-apply 'vla-get-NumberOfLoops (list obj)))
            (if (vl-catch-all-error-p area-r)
              (princ (strcat "\nArea nativa: INDISPONIVEL (" (vl-catch-all-error-message area-r) ")"))
              (princ (strcat "\nArea nativa: " (rtos area-r 2 6))))
            (if (not (vl-catch-all-error-p loops-r))
              (princ (strcat " | Loops: " (itoa loops-r))))
            (setq records (vl-catch-all-apply 'qhpe:make-hatch-records (list ent space)))
            (if (vl-catch-all-error-p records)
              (princ (strcat "\nContornos QHPE: FALHA (" (vl-catch-all-error-message records) ")"))
              (if records
                (princ (strcat "\nContornos QHPE: OK - " (itoa (length records)) " regiao(oes) valida(s)."))
                (princ "\nContornos QHPE: NENHUMA REGIAO VALIDA. Verifique boundary aberto, autointersecao, ilhas inconsistentes ou geometria degenerada."))))
          (princ "\nObjeto HATCH inacessivel."))
        (qhpe:cleanup-temp)
        (setq i (1+ i))))
    (princ "\nNenhuma hachura selecionada."))
  (setvar "CMDECHO" old-cmdecho)
  (setvar "OSMODE" old-osmode)
  (princ))

;;; ================================================================
;;; QHPE v3.3 - CORRECAO DAS TABELAS E AFASTAMENTO DAS ESTACAS
;;;
;;; - Corrige o posicionamento vertical das TABLEs. Em AutoLISP, OR
;;;   retorna apenas T/NIL; portanto ele nao pode ser usado para escolher
;;;   diretamente entre duas coordenadas numericas. A v3.2 acabava
;;;   passando T como coordenada Y, gerando "bad argument type: numberp: T".
;;; - O texto das estacas principais passa a ficar 1.50 x a altura do
;;;   texto abaixo do vertice inferior da linha de estaca.
;;; ================================================================

(defun qhpe:mostrar-ajuda-comandos ()
  (princ "\n===============================================================")
  (princ "\nQHPE v3.3 - COMANDOS DISPONIVEIS")
  (princ "\n===============================================================")
  (princ "\nQHPEGERAR          - Gera conjunto completo: diagrama, tabelas, resumo e Excel opcional.")
  (princ "\nQHPED               - Gera somente o diagrama linear.")
  (princ "\nQHPEA               - Calcula e insere somente as tabelas por estaca.")
  (princ "\nQHPERESUMO          - Insere legenda e totais gerais.")
  (princ "\nQHPEUPDATE          - Atualiza um conjunto gerado pelo QHPEGERAR.")
  (princ "\nQHPEEXPORT          - Exporta novamente os quantitativos para Excel.")
  (princ "\nQHPEESTILOTABELAS   - Aplica os estilos QHPE nas tabelas existentes.")
  (princ "\nQHPEESCALATABELAS   - Escala tabelas mantendo o centro visual.")
  (princ "\nQHPECORRIGIRTABELAS - Corrige alturas/larguras de tabelas QHPE existentes.")
  (princ "\nQHPEDIAGHATCH       - Diagnostica HATCH sem Area/contorno valido.")
  (princ "\nQHPE                - Quantificacao tradicional com tabela/CSV.")
  (princ "\nQHPEAJUDA           - Exibe esta lista.")
  (princ "\nOBS.: ESQUERDA/DIREITA sao definidas pelo sentido crescente da polilinha do eixo.")
  (princ "\n===============================================================")
  (princ))


;;; ================================================================
;;; QHPE v3.4 - AJUSTES DE TABELAS, LEGENDA E LIMPEZA DE COMANDOS
;;; ================================================================
;;; OBJETIVO DESTA REVISAO:
;;; - Retirar o comando legado QHPE da interface publica;
;;; - Fazer a base inferior das TABLEs coincidir exatamente com o ultimo
;;;   vertice (topo) da linha de estaca usada como referencia;
;;; - Evitar quebra de nomes longos na coluna SERVICO;
;;; - Reaproveitar QHPECORRIGIRTABELAS para alargar automaticamente a coluna
;;;   SERVICO em todas as tabelas selecionadas e corrigir alturas de linhas;
;;; - Fazer a legenda/totais reproduzir a mesma simbologia do diagrama:
;;;   pavimentos com preenchimento do layer + borda branca e meios-fios com
;;;   suas barras reta/curva, usando amostras graficas maiores.
;;; ================================================================

(setq qhpe:*version* "3.4")

;;; O calculo anterior por numero de caracteres podia subestimar ROMANS.SHX.
;;; TEXTBOX nao cria entidades e e rapido; usamos o maior entre a medicao real
;;; e uma estimativa conservadora.
(defun qhpe24:safe-text-width (text height style / measured estimated)
  (setq measured (qhpe22:text-width text height style)
        estimated
          (* (float (qhpe24:visible-text-length text)) height 0.76))
  (max estimated (* 1.14 measured))
)

;;; Posiciona a TABLE por sua InsertionPoint nativa. A InsertionPoint de uma
;;; TABLE horizontal e o canto superior esquerdo; portanto Y = base + Height.
(defun qhpe34:align-table-bottom (table bottom / ip height p)
  (if (and (qhpe:object-alive-p table) (numberp bottom))
    (progn
      (setq ip (qhpe27:safe-call 'vla-get-InsertionPoint (list table))
            height (qhpe27:safe-call 'vla-get-Height (list table)))
      (if (and ip (numberp height) (> height 0.0))
        (progn
          (setq p (qhpe:array->list ip))
          (if (and (listp p) (numberp (car p)) (numberp (cadr p)))
            (qhpe27:safe-call
              'vla-put-InsertionPoint
              (list table
                (vlax-3d-point
                  (list (car p) (+ bottom height)
                    (if (caddr p) (caddr p) 0.0))))))))))
  table
)

(defun qhpe23:draw-native-tables
  (insertion area-rows layers curb-rows start-station station-step linear-factor
    band-height text-height annotation-layer include-curb
    / acad doc x0 y0 geometry band-top area-accum curb-accum-straight
      curb-accum-curved index area-row curb-row layer current-area d0 d1 mid-x
      available-width table-bottom table-data title data-rows style total-count
      created-table align-items qhpe34_item)

  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        qhpe30:*space-cache* (vla-get-ModelSpace doc)
        qhpe30:*pending-tables* nil
        x0 (car insertion)
        y0 (cadr insertion)
        geometry
          (qhpe23:diagram-geometry
            insertion station-step (length layers) band-height text-height
            include-curb)
        band-top (nth 3 geometry)
        area-accum nil
        curb-accum-straight 0.0
        curb-accum-curved 0.0
        index 0
        total-count (length area-rows)
        style (qhpe21:ensure-romans-style)
        align-items nil)

  (princ
    (strcat "\nInserindo " (itoa total-count)
      " objetos TABLE em modo otimizado..."))
  (foreach area-row area-rows
    (foreach layer layers
      (setq current-area
        (max 0.0 (qhpe:row-area (nth 3 area-row) layer))
            area-accum
              (qhpe:update-accumulated area-accum layer current-area)))
    (setq curb-row (if include-curb (nth index curb-rows) nil))
    (if include-curb
      (setq curb-accum-straight
              (+ curb-accum-straight (qhpe23:curb-row-value curb-row 'RETO))
            curb-accum-curved
              (+ curb-accum-curved (qhpe23:curb-row-value curb-row 'CURVO))))

    (setq d0 (nth 4 area-row)
          d1 (nth 5 area-row))
    (if (and (numberp d0) (numberp d1) (> d1 d0))
      (progn
        (setq mid-x (+ x0 (/ (+ d0 d1) 2.0))
              available-width
                (max (* 2.0 text-height)
                     (- d1 d0 (* 0.35 text-height)))
              table-bottom (qhpe31:station-top-y insertion)
              table-bottom (if (numberp table-bottom) table-bottom band-top)
              table-data
                (qhpe23:table-data
                  area-row layers area-accum curb-row
                  curb-accum-straight curb-accum-curved linear-factor
                  start-station station-step include-curb)
              title (car table-data)
              data-rows (cadr table-data))
        (setq created-table
          (qhpe23:create-native-table
            mid-x available-width table-bottom title data-rows
            text-height annotation-layer style))
        (if created-table
          (setq align-items
            (cons (list created-table table-bottom) align-items)))))
    (setq index (1+ index))
    (if (= 0 (rem index 10))
      (princ (strcat "\rTabelas: " (itoa index) "/" (itoa total-count)))))

  (qhpe30:release-pending-tables)
  ;; Uma unica regeneracao garante que a altura nativa de cada TABLE esteja
  ;; atualizada. Em seguida a propria InsertionPoint da TABLE e ajustada para
  ;; que sua borda inferior coincida exatamente com o ultimo vertice da linha
  ;; da estaca (table-bottom).
  (vla-Regen doc 1)
  (foreach qhpe34_item align-items
    (qhpe34:align-table-bottom (car qhpe34_item) (cadr qhpe34_item)))
  (setq qhpe30:*space-cache* nil)
  (princ (strcat "\rTabelas: " (itoa total-count) "/" (itoa total-count) "."))
  T
)

(defun qhpe25:create-summary-table
  (bottom-left rows preferred-height annotation-layer / acad doc space style
    title table-rows service-texts unit-texts total-texts row height margin-x
    margin-y w1 w2 w3 total-width title-height row-height row-count table-height
    top-left table row-index data-row swatch-x0 swatch-x1 swatch-y0 swatch-y1
    swatch-center layer-name)

  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        style (qhpe21:ensure-romans-style)
        title "LEGENDA E TOTAIS"
        table-rows rows
        height preferred-height
        service-texts (list "SERVI\\U+00C7O")
        unit-texts (list "UNIDADE")
        total-texts (list "TOTAL"))
  (foreach row rows
    (setq service-texts (cons (nth 0 row) service-texts)
          unit-texts (cons (nth 1 row) unit-texts)
          total-texts (cons (nth 2 row) total-texts)))
  (setq margin-x (* 0.62 height)
        margin-y (* 0.32 height)
        w1 (+ (qhpe24:max-safe-text-width service-texts height style)
              (* 2.0 margin-x))
        w2 (+ (qhpe24:max-safe-text-width unit-texts height style)
              (* 2.0 margin-x))
        w3 (+ (qhpe24:max-safe-text-width total-texts height style)
              (* 2.0 margin-x))
        total-width (+ w1 w2 w3)
        title-height (+ (* 1.30 height) (* 2.0 margin-y))
        row-height (+ (* 1.18 height) (* 2.0 margin-y))
        row-count (+ 2 (length rows))
        table-height (+ title-height (* (1- row-count) row-height))
        top-left
          (list (car bottom-left) (+ (cadr bottom-left) table-height) 0.0))

  (setq table
    (vla-AddTable space (vlax-3d-point top-left) row-count 3 row-height w1))
  (if table
    (progn
      (qhpe23:safe-table-call 'vla-put-Layer (list table annotation-layer))
      (qhpe27:apply-table-style table)
      (qhpe23:safe-table-call 'vla-put-RegenerateTableSuppressed
        (list table :vlax-true))
      (qhpe23:safe-table-call 'vla-put-HorzCellMargin (list table margin-x))
      (qhpe23:safe-table-call 'vla-put-VertCellMargin (list table margin-y))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 0 w1))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 1 w2))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 2 w3))
      (qhpe23:safe-table-call 'vla-SetRowHeight (list table 0 title-height))
      (setq row-index 1)
      (while (< row-index row-count)
        (qhpe23:safe-table-call 'vla-SetRowHeight
          (list table row-index row-height))
        (setq row-index (1+ row-index)))
      (qhpe23:safe-table-call 'vla-MergeCells (list table 0 0 0 2))
      (qhpe23:safe-table-call 'vla-SetTextStyle
        (list table (+ qhpe:acTitleRow qhpe:acHeaderRow qhpe:acDataRow) style))
      (qhpe23:safe-table-call 'vla-SetTextHeight
        (list table (+ qhpe:acTitleRow qhpe:acHeaderRow qhpe:acDataRow) height))
      (qhpe:set-table-text table 0 0 title)
      (qhpe:set-table-text table 1 0 "SERVI\\U+00C7O")
      (qhpe:set-table-text table 1 1 "UNIDADE")
      (qhpe:set-table-text table 1 2 "TOTAL")
      (qhpe23:safe-table-call 'vla-SetCellAlignment
        (list table 0 0 qhpe23:acMiddleCenter))
      (setq row-index 0)
      (while (< row-index 3)
        (qhpe23:safe-table-call 'vla-SetCellAlignment
          (list table 1 row-index qhpe23:acMiddleCenter))
        (setq row-index (1+ row-index)))
      (setq data-row 2)
      (foreach row rows
        (qhpe:set-table-text table data-row 0 (nth 0 row))
        (qhpe:set-table-text table data-row 1 (nth 1 row))
        (qhpe:set-table-text table data-row 2 (nth 2 row))
        (qhpe23:safe-table-call 'vla-SetCellAlignment
          (list table data-row 0 qhpe23:acMiddleLeft))
        (qhpe23:safe-table-call 'vla-SetCellAlignment
          (list table data-row 1 qhpe23:acMiddleCenter))
        (qhpe23:safe-table-call 'vla-SetCellAlignment
          (list table data-row 2 qhpe23:acMiddleRight))
        (setq data-row (1+ data-row)))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 0 w1))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 1 w2))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 2 w3))
      (qhpe23:safe-table-call 'vla-put-RegenerateTableSuppressed
        (list table :vlax-false))
      (qhpe23:safe-table-call 'vla-RecomputeTableBlock (list table :vlax-true))
      (qhpe23:safe-table-call 'vla-Update (list table))

      ;; Legenda grafica maior e com a MESMA representacao do diagrama:
      ;; pavimentos = SOLID no layer original + borda WHITE;
      ;; meios-fios = barras nos layers reto/curvo correspondentes.
      (setq swatch-x1 (- (car top-left) (* 0.38 height))
            swatch-x0 (- swatch-x1 (* 2.10 height))
            data-row 0)
      (foreach row rows
        (setq swatch-center
          (- (cadr top-left) title-height row-height
            (* (+ data-row 0.5) row-height))
              swatch-y0 (- swatch-center (* 0.40 row-height))
              swatch-y1 (+ swatch-center (* 0.40 row-height))
              layer-name (nth 3 row))
        (if (and layer-name (tblsearch "LAYER" layer-name))
          (if (= (nth 5 row) "AREA")
            (progn
              (qhpe21:make-solid swatch-x0 swatch-y0 swatch-x1 swatch-y1 layer-name)
              (qhpe31:make-white-rectangle
                swatch-x0 swatch-y0 swatch-x1 swatch-y1 layer-name))
            (qhpe21:make-band
              swatch-x0 swatch-x1 swatch-y0 swatch-y1 layer-name)))
        (setq data-row (1+ data-row)))
    )
  )
  (list table total-width table-height bottom-left)
)

;;; ----------------------------------------------------------------
;;; QHPECORRIGIRTABELAS - NOVA FUNCAO
;;; ----------------------------------------------------------------
;;; Mede os textos reais da coluna SERVICO de todas as TABLEs selecionadas,
;;; escolhe uma largura unica suficiente para o maior texto e aplica essa
;;; largura a todas. Mantem o centro horizontal de cada tabela e normaliza
;;; as alturas das linhas para eliminar linhas extras causadas por quebra.
;;; ----------------------------------------------------------------

(defun qhpe34:get-cell-text (table row col / result)
  (setq result
    (vl-catch-all-apply 'vla-GetText (list table row col)))
  (if (vl-catch-all-error-p result) "" result)
)

(defun qhpe34:get-column-width (table col / result)
  (setq result
    (vl-catch-all-apply 'vla-GetColumnWidth (list table col)))
  (if (and (not (vl-catch-all-error-p result))
           (numberp result))
    result
    0.0)
)

(defun qhpe34:required-service-width
  (table / rows r text data-height header-height h margin style width max-width)
  (setq rows
          (qhpe31:safe-number
            (qhpe27:safe-call 'vla-get-Rows (list table)) 0)
        data-height (qhpe31:get-text-height table qhpe:acDataRow 0.30)
        header-height (qhpe31:get-text-height table qhpe:acHeaderRow data-height)
        margin
          (qhpe31:safe-number
            (qhpe27:safe-call 'vla-get-HorzCellMargin (list table))
            (* 0.55 data-height))
        style (qhpe21:ensure-romans-style)
        max-width 0.0
        r 1)
  (while (< r rows)
    (setq text (qhpe34:get-cell-text table r 0)
          h (if (= r 1) header-height data-height)
          width
            (+ (qhpe24:safe-text-width text h style)
               (* 2.0 margin)
               (* 0.30 h)))
    (if (> width max-width) (setq max-width width))
    (setq r (1+ r)))
  (max max-width (qhpe34:get-column-width table 0))
)

(defun qhpe34:selection-service-width
  (selection / i ent table width target)
  (setq i 0 target 0.0)
  (if selection
    (repeat (sslength selection)
      (setq ent (ssname selection i)
            table (qhpe:safe-vla-object ent))
      (if (and table (= (vla-get-ObjectName table) "AcDbTable"))
        (progn
          (setq width (qhpe34:required-service-width table))
          (if (> width target) (setq target width))))
      (setq i (1+ i))))
  target
)

(defun qhpe34:widen-service-column
  (table target / old-width insertion p delta result)
  (if (and table (= (vla-get-ObjectName table) "AcDbTable")
           (numberp target) (> target 0.0))
    (progn
      (setq old-width (qhpe34:get-column-width table 0)
            insertion (qhpe27:safe-call 'vla-get-InsertionPoint (list table)))
      (if (and (numberp old-width) (> target (+ old-width 1.0e-8)))
        (progn
          (setq delta (- target old-width))
          (qhpe23:safe-table-call
            'vla-put-RegenerateTableSuppressed (list table :vlax-true))
          (setq result
            (qhpe23:safe-table-call
              'vla-SetColumnWidth (list table 0 target)))
          (qhpe23:safe-table-call
            'vla-put-RegenerateTableSuppressed (list table :vlax-false))

          ;; Corrige alturas de linhas que tenham crescido por quebra automatica.
          (qhpe31:normalize-existing-table table)

          ;; Mantem o centro horizontal da tabela no mesmo ponto.
          (if insertion
            (progn
              (setq p (qhpe:array->list insertion))
              ;; normalize-existing-table pode ter alterado apenas Y; lemos de novo.
              (setq insertion
                (qhpe27:safe-call 'vla-get-InsertionPoint (list table)))
              (if insertion
                (setq p (qhpe:array->list insertion)))
              (if (and (listp p) (numberp (car p)) (numberp (cadr p)))
                (qhpe27:safe-call
                  'vla-put-InsertionPoint
                  (list table
                    (vlax-3d-point
                      (list (- (car p) (/ delta 2.0))
                        (cadr p)
                        (if (caddr p) (caddr p) 0.0))))))))
          T)
        (progn
          ;; Mesmo sem alargar, normaliza as linhas.
          (qhpe31:normalize-existing-table table)
          T)))
    nil)
)

(defun c:QHPECORRIGIRTABELAS
  (/ acad doc scope selection target index ent table count
    old-regenmode old-cmdecho)
  (vl-load-com)
  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        scope (qhpe27:prompt-table-scope)
        selection (qhpe27:table-selection scope))
  (if (null selection)
    (princ "\nNenhuma tabela QHPE encontrada.")
    (progn
      (setq target (qhpe34:selection-service-width selection))
      (if (<= target 0.0)
        (princ "\nNao foi possivel determinar a largura necessaria.")
        (progn
          (setq old-regenmode (getvar "REGENMODE")
                old-cmdecho (getvar "CMDECHO"))
          (setvar "REGENMODE" 0)
          (setvar "CMDECHO" 0)
          (vla-StartUndoMark doc)
          (setq index 0 count 0)
          (repeat (sslength selection)
            (setq ent (ssname selection index)
                  table (qhpe:safe-vla-object ent))
            (if (qhpe34:widen-service-column table target)
              (setq count (1+ count)))
            (setq index (1+ index)))
          (vla-EndUndoMark doc)
          (setvar "REGENMODE" old-regenmode)
          (setvar "CMDECHO" old-cmdecho)
          (vla-Regen doc 1)
          (princ
            (strcat
              "\nColuna SERVICO ajustada para largura "
              (rtos target 2 3) " em " (itoa count) " tabela(s)."))))))
  (princ)
)

;;; O comando legado fica neutralizado em sessoes onde uma versao anterior
;;; ja estava carregada. Ele nao e mais listado nem executa a rotina antiga.
(defun c:QHPE ()
  (princ
    "\nO comando QHPE legado foi removido. Use QHPEGERAR, QHPED ou QHPEA.")
  (princ)
)

(defun qhpe:mostrar-ajuda-comandos ()
  (princ "\n===============================================================")
  (princ "\nQHPE v3.4 - COMANDOS DISPONIVEIS")
  (princ "\n===============================================================")
  (princ "\nQHPEGERAR           - Gera tudo: diagrama, tabelas, resumo/totais e Excel opcional.")
  (princ "\nQHPED                - Gera somente o diagrama linear dos pavimentos e meios-fios.")
  (princ "\nQHPEA                - Calcula e insere somente as tabelas por estaca.")
  (princ "\nQHPERESUMO           - Insere legenda grafica e tabela geral de totais.")
  (princ "\nQHPEUPDATE           - Atualiza um conjunto existente gerado pelo QHPEGERAR.")
  (princ "\nQHPEEXPORT           - Exporta novamente os quantitativos formatados para o Excel.")
  (princ "\nQHPEESTILOTABELAS    - Reaplica os estilos QHPE nas tabelas existentes.")
  (princ "\nQHPEESCALATABELAS    - Escala tabelas mantendo o centro visual.")
  (princ "\nQHPECORRIGIRTABELAS  - Alarga a coluna SERVICO e corrige linhas com texto quebrado.")
  (princ "\nQHPEDIAGHATCH        - Diagnostica HATCH sem Area nativa/contorno valido.")
  (princ "\nQHPEAJUDA            - Exibe esta lista de comandos.")
  (princ "\nOBS.: ESQUERDA/DIREITA seguem o sentido crescente da polilinha do eixo.")
  (princ "\n===============================================================")
  (princ)
)

(princ)


;;; ===========================================================================
;;; QHPE v3.5 - NOME DA VIA
;;; - QHPEGERAR e QHPERESUMO pedem o nome da via (digitado ou lido de um
;;;   TEXTO/MTEXTO do desenho).
;;; - O nome entra no titulo da tabela LEGENDA E TOTAIS, nos titulos das abas
;;;   do Excel e no nome sugerido do arquivo .xlsx (salvo automaticamente).
;;; - O nome fica gravado no conjunto (grupo QHPE25_*), entao QHPEUPDATE e
;;;   QHPEEXPORT o mantem.
;;; - QHPEEXPORTTODAS: exporta todas as vias do desenho em um unico Excel
;;;   (aba Resumo Geral + uma aba por via).
;;; - QHPENOMEVIA: define/altera o nome de um conjunto ja existente.
;;; ===========================================================================

(setq qhpe:*version* "3.5")
(if (/= (type qhpe35:*street-name*) 'STR) (setq qhpe35:*street-name* ""))

;;; ---------------------------------------------------------------- textos ---

(defun qhpe35:trim (text)
  (if (= (type text) 'STR) (vl-string-trim " \t\r\n" text) "")
)

(defun qhpe35:collapse-spaces (text)
  (while (vl-string-search "  " text)
    (setq text (vl-string-subst " " "  " text)))
  text
)

(defun qhpe35:strip-chars (text chars / result index ch)
  (setq result "" index 1)
  (while (<= index (strlen text))
    (setq ch (substr text index 1))
    (if (not (vl-string-search ch chars))
      (setq result (strcat result ch)))
    (setq index (1+ index)))
  result
)

;;; Remove codigos de formatacao de MTEXT (\P, \A1;, \C3;, \fArial|b0;, {}, etc.).
(defun qhpe35:mtext-plain (text / result index len ch next found)
  (setq result "" index 1 len (strlen text))
  (while (<= index len)
    (setq ch (substr text index 1))
    (cond
      ((= ch "\\")
        (setq next (substr text (1+ index) 1))
        (cond
          ((member next '("\\" "{" "}"))
            (setq result (strcat result next) index (+ index 2)))
          ((member next '("P" "~"))
            (setq result (strcat result " ") index (+ index 2)))
          ((member next '("L" "l" "O" "o" "K" "k"))
            (setq index (+ index 2)))
          ((member next '("A" "C" "c" "F" "f" "H" "h" "Q" "q" "T" "t" "W" "w" "p" "S"))
            ;; codigo com parametros terminado em ";"
            (setq found (vl-string-search ";" text index))
            (setq index (if found (+ found 2) (1+ len))))
          (T
            (setq result (strcat result next) index (+ index 2)))))
      ((member ch '("{" "}"))
        (setq index (1+ index)))
      (T
        (setq result (strcat result ch) index (1+ index)))))
  result
)

(defun qhpe35:text-plain (text)
  (foreach code '("%%u" "%%U" "%%o" "%%O" "%%k" "%%K")
    (while (vl-string-search code text)
      (setq text (vl-string-subst "" code text))))
  text
)

(defun qhpe35:clean-name (text)
  (qhpe35:trim
    (qhpe35:collapse-spaces
      (vl-string-translate "\t\r\n" "   " (if (= (type text) 'STR) text ""))))
)

;;; Le o conteudo de um TEXT ou MTEXT; nil para outros objetos.
(defun qhpe35:entity-text (ent / data kind raw)
  (setq data (entget ent)
        kind (cdr (assoc 0 data))
        raw nil)
  (cond
    ((= kind "TEXT")
      (setq raw (qhpe35:text-plain (cdr (assoc 1 data)))))
    ((= kind "MTEXT")
      (setq raw (vl-catch-all-apply 'vla-get-TextString
                  (list (vlax-ename->vla-object ent))))
      (if (vl-catch-all-error-p raw)
        (setq raw nil)
        (setq raw (qhpe35:mtext-plain raw)))))
  (if raw (qhpe35:clean-name raw) nil)
)

;;; "BASE" -> "BASE - NOME DA VIA" (ou so "BASE" sem nome).
(defun qhpe35:titled (base)
  (if (qhpe25:str-empty-p qhpe35:*street-name*)
    base
    (strcat base " - " qhpe35:*street-name*))
)

;;; Mesmo titulo, sem caracteres que o MTEXT da tabela interpretaria.
(defun qhpe35:table-safe (text)
  (qhpe35:strip-chars text "\\{}")
)

(defun qhpe35:table-titled (base)
  (if (qhpe25:str-empty-p qhpe35:*street-name*)
    base
    (strcat base " - " (qhpe35:table-safe qhpe35:*street-name*)))
)

;;; Parte de nome de arquivo: remove \ / : * ? " < > |
(defun qhpe35:safe-file-part (text)
  (qhpe35:clean-name (qhpe35:strip-chars (if text text "") "\\/:*?\"<>|"))
)

(defun qhpe35:member-ci (text items)
  (vl-some
    (function (lambda (item) (= (strcase item) (strcase text))))
    items)
)

;;; Nome de aba do Excel: sem [ ] : * ? / \ ' , max. 31 caracteres, unico.
(defun qhpe35:unique-sheet-name (name used / base candidate number suffix)
  (setq base (qhpe35:clean-name (qhpe35:strip-chars (if name name "") "[]:*?/\\'")))
  (if (= base "") (setq base "Via"))
  (if (> (strlen base) 31) (setq base (qhpe35:trim (substr base 1 31))))
  (setq candidate base number 2)
  (while (qhpe35:member-ci candidate used)
    (setq suffix (strcat " (" (itoa number) ")")
          candidate
            (strcat
              (qhpe35:trim (substr base 1 (min (strlen base) (- 31 (strlen suffix)))))
              suffix)
          number (1+ number)))
  candidate
)

;;; ------------------------------------------------------ nome da via -------

(defun qhpe35:type-street-name (current / answer)
  (setq answer
    (getstring T
      (strcat "\nDigite o nome da via"
        (if (qhpe25:str-empty-p current) " (Enter = sem nome): "
          (strcat " <" current ">: ")))))
  (setq answer (qhpe35:clean-name answer))
  (if (= answer "") current answer)
)

;;; Pergunta o nome da via. Aceita selecionar um TEXTO/MTEXTO do desenho
;;; (ex.: o rotulo da rua) ou digitar. Retorna o nome ("" = sem nome).
(defun qhpe35:prompt-street-name (current / done result selection text)
  (if (/= (type current) 'STR) (setq current ""))
  (setq done nil result "")
  (while (not done)
    (setvar "ERRNO" 0)
    (initget "Digitar")
    (setq selection
      (entsel
        (strcat "\nNome da via: selecione um TEXTO/MTEXTO com o nome ou [Digitar]"
          (if (= current "") " <Digitar>: " (strcat " <" current ">: ")))))
    (cond
      ((equal selection "Digitar")
        (setq result (qhpe35:type-street-name current) done T))
      ((and (null selection) (= (getvar "ERRNO") 7))
        (princ "\nNada selecionado. Clique no texto ou digite D para digitar."))
      ((null selection)
        (setq result
          (if (= current "") (qhpe35:type-street-name current) current)
          done T))
      (T
        (setq text (qhpe35:entity-text (car selection)))
        (if (qhpe25:str-empty-p text)
          (princ "\nO objeto nao e um TEXTO/MTEXTO com conteudo. Tente de novo.")
          (setq result text done T)))))
  (setq result (qhpe35:clean-name result))
  (if (= result "")
    (princ "\nVia sem nome: tabela e Excel ficam sem identificacao da rua.")
    (princ (strcat "\nVia: " result)))
  result
)

;;; --------------------------------------------------- metadados do grupo ---

(defun qhpe25:metadata-string
  (axis hatch-ss curb-layer station-step start-station search-width linear-factor
    insertion text-height band-height summary-point / metadata)
  (setq metadata
    (list
      (cons 'V "2.5")
      (cons 'A (qhpe25:handle-of axis))
      (cons 'H (qhpe25:ss-handles hatch-ss))
      (cons 'C (if curb-layer curb-layer ""))
      (cons 'S station-step)
      (cons 'ST start-station)
      (cons 'W search-width)
      (cons 'WR
        (if (and (numberp qhpe:*right-width-current*) (> qhpe:*right-width-current* 0.0))
          qhpe:*right-width-current*
          search-width))
      (cons 'F linear-factor)
      (cons 'I insertion)
      (cons 'TH text-height)
      (cons 'BH band-height)
      (cons 'L summary-point)
      (cons 'N (if (= (type qhpe35:*street-name*) 'STR) qhpe35:*street-name* ""))))
  (vl-prin1-to-string metadata)
)

(defun qhpe35:metadata-street-name (metadata / value)
  (setq value (qhpe25:metadata-value 'N metadata))
  (if (= (type value) 'STR) value "")
)

;;; Alem das fontes, restaura o nome da via gravado no conjunto (QHPEUPDATE,
;;; QHPEEXPORT e QHPEEXPORTTODAS passam por aqui).
(defun qhpe25:resolve-metadata-sources
  (metadata / axis hatch-ss curb-layer)
  (setq qhpe35:*street-name* (qhpe35:metadata-street-name metadata))
  (setq axis (handent (qhpe25:metadata-value 'A metadata))
        hatch-ss
          (qhpe25:ss-from-handles (qhpe25:metadata-value 'H metadata) "HATCH")
        curb-layer (qhpe25:metadata-value 'C metadata))
  (if (= curb-layer "") (setq curb-layer nil))
  (list axis hatch-ss curb-layer)
)

;;; v3.5: tabela LEGENDA E TOTAIS com o nome da via no titulo.
(defun qhpe25:create-summary-table
  (bottom-left rows preferred-height annotation-layer / acad doc space style
    title table-rows service-texts unit-texts total-texts row height margin-x
    margin-y w1 w2 w3 total-width title-height row-height row-count table-height
    top-left table row-index data-row swatch-x0 swatch-x1 swatch-y0 swatch-y1
    swatch-center layer-name title-width)

  (setq acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        style (qhpe21:ensure-romans-style)
        title (qhpe35:table-titled "LEGENDA E TOTAIS")
        table-rows rows
        height preferred-height
        service-texts (list "SERVI\\U+00C7O")
        unit-texts (list "UNIDADE")
        total-texts (list "TOTAL"))
  (foreach row rows
    (setq service-texts (cons (nth 0 row) service-texts)
          unit-texts (cons (nth 1 row) unit-texts)
          total-texts (cons (nth 2 row) total-texts)))
  (setq margin-x (* 0.62 height)
        margin-y (* 0.32 height)
        w1 (+ (qhpe24:max-safe-text-width service-texts height style)
              (* 2.0 margin-x))
        w2 (+ (qhpe24:max-safe-text-width unit-texts height style)
              (* 2.0 margin-x))
        w3 (+ (qhpe24:max-safe-text-width total-texts height style)
              (* 2.0 margin-x))
        total-width (+ w1 w2 w3)
        title-height (+ (* 1.30 height) (* 2.0 margin-y))
        row-height (+ (* 1.18 height) (* 2.0 margin-y))
        row-count (+ 2 (length rows))
        table-height (+ title-height (* (1- row-count) row-height))
        top-left
          (list (car bottom-left) (+ (cadr bottom-left) table-height) 0.0))

  ;; v3.5: o titulo (com o nome da via) precisa caber na largura da tabela.
  (setq title-width
    (+ (* 1.15 (qhpe24:max-safe-text-width (list title) height style))
       (* 2.0 margin-x)))
  (if (> title-width total-width)
    (setq w1 (+ w1 (- title-width total-width))
          total-width title-width))

  (setq table
    (vla-AddTable space (vlax-3d-point top-left) row-count 3 row-height w1))
  (if table
    (progn
      (qhpe23:safe-table-call 'vla-put-Layer (list table annotation-layer))
      (qhpe27:apply-table-style table)
      (qhpe23:safe-table-call 'vla-put-RegenerateTableSuppressed
        (list table :vlax-true))
      (qhpe23:safe-table-call 'vla-put-HorzCellMargin (list table margin-x))
      (qhpe23:safe-table-call 'vla-put-VertCellMargin (list table margin-y))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 0 w1))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 1 w2))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 2 w3))
      (qhpe23:safe-table-call 'vla-SetRowHeight (list table 0 title-height))
      (setq row-index 1)
      (while (< row-index row-count)
        (qhpe23:safe-table-call 'vla-SetRowHeight
          (list table row-index row-height))
        (setq row-index (1+ row-index)))
      (qhpe23:safe-table-call 'vla-MergeCells (list table 0 0 0 2))
      (qhpe23:safe-table-call 'vla-SetTextStyle
        (list table (+ qhpe:acTitleRow qhpe:acHeaderRow qhpe:acDataRow) style))
      (qhpe23:safe-table-call 'vla-SetTextHeight
        (list table (+ qhpe:acTitleRow qhpe:acHeaderRow qhpe:acDataRow) height))
      (qhpe:set-table-text table 0 0 title)
      (qhpe:set-table-text table 1 0 "SERVI\\U+00C7O")
      (qhpe:set-table-text table 1 1 "UNIDADE")
      (qhpe:set-table-text table 1 2 "TOTAL")
      (qhpe23:safe-table-call 'vla-SetCellAlignment
        (list table 0 0 qhpe23:acMiddleCenter))
      (setq row-index 0)
      (while (< row-index 3)
        (qhpe23:safe-table-call 'vla-SetCellAlignment
          (list table 1 row-index qhpe23:acMiddleCenter))
        (setq row-index (1+ row-index)))
      (setq data-row 2)
      (foreach row rows
        (qhpe:set-table-text table data-row 0 (nth 0 row))
        (qhpe:set-table-text table data-row 1 (nth 1 row))
        (qhpe:set-table-text table data-row 2 (nth 2 row))
        (qhpe23:safe-table-call 'vla-SetCellAlignment
          (list table data-row 0 qhpe23:acMiddleLeft))
        (qhpe23:safe-table-call 'vla-SetCellAlignment
          (list table data-row 1 qhpe23:acMiddleCenter))
        (qhpe23:safe-table-call 'vla-SetCellAlignment
          (list table data-row 2 qhpe23:acMiddleRight))
        (setq data-row (1+ data-row)))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 0 w1))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 1 w2))
      (qhpe23:safe-table-call 'vla-SetColumnWidth (list table 2 w3))
      (qhpe23:safe-table-call 'vla-put-RegenerateTableSuppressed
        (list table :vlax-false))
      (qhpe23:safe-table-call 'vla-RecomputeTableBlock (list table :vlax-true))
      (qhpe23:safe-table-call 'vla-Update (list table))

      ;; Legenda grafica maior e com a MESMA representacao do diagrama:
      ;; pavimentos = SOLID no layer original + borda WHITE;
      ;; meios-fios = barras nos layers reto/curvo correspondentes.
      (setq swatch-x1 (- (car top-left) (* 0.38 height))
            swatch-x0 (- swatch-x1 (* 2.10 height))
            data-row 0)
      (foreach row rows
        (setq swatch-center
          (- (cadr top-left) title-height row-height
            (* (+ data-row 0.5) row-height))
              swatch-y0 (- swatch-center (* 0.40 row-height))
              swatch-y1 (+ swatch-center (* 0.40 row-height))
              layer-name (nth 3 row))
        (if (and layer-name (tblsearch "LAYER" layer-name))
          (if (= (nth 5 row) "AREA")
            (progn
              (qhpe21:make-solid swatch-x0 swatch-y0 swatch-x1 swatch-y1 layer-name)
              (qhpe31:make-white-rectangle
                swatch-x0 swatch-y0 swatch-x1 swatch-y1 layer-name))
            (qhpe21:make-band
              swatch-x0 swatch-x1 swatch-y0 swatch-y1 layer-name)))
        (setq data-row (1+ data-row)))
    )
  )
  (list table total-width table-height bottom-left)
)

;;; ------------------------------------------------------------- Excel ------

;;; Pergunta onde salvar. Sugestao: <pasta do DWG>\<DWG>_QHPE_<sufixo>.xlsx
(defun qhpe35:ask-excel-path (suffix / default path)
  (setq default
    (strcat (getvar "DWGPREFIX")
      (vl-filename-base (getvar "DWGNAME"))
      "_QHPE"
      (if (qhpe25:str-empty-p suffix) "" (strcat "_" suffix))
      ".xlsx"))
  (setq path (getfiled "Salvar quantitativos QHPE (Excel)" default "xlsx" 1))
  (if path
    (progn
      (if (not (wcmatch (strcase path) "*.XLSX"))
        (setq path (strcat path ".xlsx")))
      path)
    (progn
      (princ "\nSalvamento cancelado: a planilha sera aberta sem salvar.")
      nil))
)

(defun qhpe35:save-workbook (excel book path / result)
  (if (qhpe25:str-empty-p path)
    nil
    (progn
      ;; O getfiled ja confirmou a substituicao; evita o segundo aviso do Excel.
      (vl-catch-all-apply 'vlax-put-property (list excel 'DisplayAlerts :vlax-false))
      (setq result (vl-catch-all-apply 'vlax-invoke-method (list book 'SaveAs path 51)))
      (vl-catch-all-apply 'vlax-put-property (list excel 'DisplayAlerts :vlax-true))
      (if (vl-catch-all-error-p result)
        (progn
          (princ (strcat "\nNao foi possivel salvar \"" path "\": "
            (vl-catch-all-error-message result)
            "\nVerifique se o arquivo esta aberto. A planilha fica aberta sem salvar."))
          nil)
        (progn
          (princ (strcat "\nPlanilha salva em: " path))
          T))))
)

(defun qhpe25:excel-fill-summary-sheet
  (sheet rows / cells row-index final-row number-range title service unit)
  (setq cells (vlax-get-property sheet 'Cells)
        title (qhpe35:titled (strcat "RESUMO DOS " (qhpe:excel-servicos)))
        service (qhpe:excel-servico)
        unit "UNIDADE")
  (qhpe25:excel-set-cell cells 1 1 title)
  (qhpe25:excel-set-cell cells 2 1 service)
  (qhpe25:excel-set-cell cells 2 2 unit)
  (qhpe25:excel-set-cell cells 2 3 "TOTAL")
  (setq row-index 3)
  (foreach row rows
    (qhpe25:excel-set-cell cells row-index 1 (nth 0 row))
    (qhpe25:excel-set-cell cells row-index 2
      (if (= (nth 1 row) "m\\U+00B2") (qhpe:excel-m2) (nth 1 row)))
    (qhpe25:excel-set-cell cells row-index 3 (nth 4 row))
    (setq row-index (1+ row-index)))
  (setq final-row (max 2 (1- row-index)))
  (if (> final-row 2)
    (progn
      (setq number-range (qhpe25:excel-range sheet (strcat "C3:C" (itoa final-row))))
      (qhpe25:excel-apply-number-format number-range)
      (vlax-release-object number-range)))
  (qhpe25:excel-style-sheet sheet final-row 3 title)
  (vlax-release-object cells))

;;; Exportacao de uma via: titulos com o nome da via e arquivo salvo.
(defun qhpe25:export-excel
  (data start-station station-step linear-factor / excel books book sheets
    sheet1 sheet2 sheet3 summary-rows result path saved)
  (setq path (qhpe35:ask-excel-path (qhpe35:safe-file-part qhpe35:*street-name*))
        saved nil)
  (princ "\nAbrindo o Excel e montando a planilha formatada...")
  (setq result
    (vl-catch-all-apply
      (function
        (lambda ()
          (setq excel (vlax-get-or-create-object "Excel.Application")
                books (vlax-get-property excel 'Workbooks)
                book (vlax-invoke-method books 'Add)
                sheets (vlax-get-property book 'Worksheets))
          (while (< (vlax-get-property sheets 'Count) 3)
            (vlax-invoke-method sheets 'Add))
          (setq sheet1 (vlax-get-property sheets 'Item 1)
                sheet2 (vlax-get-property sheets 'Item 2)
                sheet3 (vlax-get-property sheets 'Item 3))
          (vlax-put-property sheet1 'Name "Por Estaca")
          (vlax-put-property sheet2 'Name "Acumulados")
          (vlax-put-property sheet3 'Name "Resumo")
          (qhpe25:excel-fill-station-sheet sheet1
            (qhpe35:titled "QUANTITATIVOS POR ESTACA")
            data start-station station-step linear-factor nil)
          (qhpe25:excel-fill-station-sheet sheet2
            (qhpe35:titled "ACUMULADOS POR ESTACA")
            data start-station station-step linear-factor T)
          (setq summary-rows (qhpe25:summary-rows data linear-factor))
          (qhpe25:excel-fill-summary-sheet sheet3 summary-rows)
          (vlax-invoke-method sheet1 'Activate)
          (setq saved (qhpe35:save-workbook excel book path))
          (vlax-put-property excel 'Visible :vlax-true)
          T))))
  (foreach object (list sheet1 sheet2 sheet3 sheets book books excel)
    (if (and object (not (vlax-object-released-p object)))
      (vlax-release-object object)))
  (gc)
  (if (vl-catch-all-error-p result)
    (progn
      (princ (strcat "\nFalha ao exportar para Excel: "
        (vl-catch-all-error-message result)))
      nil)
    (progn
      (if (not saved)
        (princ "\nPlanilha criada no Excel (nao salva). Use Salvar Como para guardar."))
      T))
)

;;; Aba "Resumo Geral": servicos x vias, com coluna TOTAL (formula SOMA).
(defun qhpe35:fill-general-summary
  (sheet entries / cells keys via-values values rows key col via-count
    total-col last-data-col row-index final-row number-range title value
    formula-range)
  (setq keys nil via-values nil)
  (foreach entry entries
    (setq rows (qhpe25:summary-rows (nth 1 entry) (nth 4 entry))
          values nil)
    (foreach row rows
      (setq key (list (nth 0 row) (nth 1 row)))
      (if (not (member key keys))
        (setq keys (append keys (list key))))
      (setq values (cons (cons key (nth 4 row)) values)))
    (setq via-values (append via-values (list values))))
  (setq cells (vlax-get-property sheet 'Cells)
        via-count (length entries)
        total-col (+ 3 via-count)
        last-data-col (qhpe25:column-letter (1- total-col))
        title (strcat "RESUMO GERAL DOS " (qhpe:excel-servicos) " - TODAS AS VIAS"))
  (qhpe25:excel-set-cell cells 1 1 title)
  (qhpe25:excel-set-cell cells 2 1 (qhpe:excel-servico))
  (qhpe25:excel-set-cell cells 2 2 "UNIDADE")
  (setq col 3)
  (foreach entry entries
    (qhpe25:excel-set-cell cells 2 col (car entry))
    (setq col (1+ col)))
  (qhpe25:excel-set-cell cells 2 total-col "TOTAL")
  (setq row-index 3)
  (foreach key keys
    (qhpe25:excel-set-cell cells row-index 1 (car key))
    (qhpe25:excel-set-cell cells row-index 2
      (if (= (cadr key) "m\\U+00B2") (qhpe:excel-m2) (cadr key)))
    (setq col 3)
    (foreach values via-values
      (setq value (cdr (assoc key values)))
      (qhpe25:excel-set-cell cells row-index col (if (numberp value) value 0.0))
      (setq col (1+ col)))
    (setq formula-range
      (qhpe25:excel-range sheet
        (strcat (qhpe25:column-letter total-col) (itoa row-index))))
    (vl-catch-all-apply 'vlax-put-property
      (list formula-range 'Formula
        (strcat "=SUM(C" (itoa row-index) ":" last-data-col (itoa row-index) ")")))
    (vlax-release-object formula-range)
    (setq row-index (1+ row-index)))
  (setq final-row (max 2 (1- row-index)))
  (if (> final-row 2)
    (progn
      (setq number-range
        (qhpe25:excel-range sheet
          (strcat "C3:" (qhpe25:column-letter total-col) (itoa final-row))))
      (qhpe25:excel-apply-number-format number-range)
      (vlax-release-object number-range)))
  (qhpe25:excel-style-sheet sheet final-row total-col title)
  (vlax-release-object cells)
)

;;; entries = lista de (nome data estaca-inicial passo fator)
(defun qhpe35:export-all-excel
  (entries path / excel books book sheets sheet first-sheet sheet-objects
    used-names name index result saved)
  (setq saved nil sheet-objects nil used-names (list "Resumo Geral"))
  (princ "\nAbrindo o Excel e montando a planilha de todas as vias...")
  (setq result
    (vl-catch-all-apply
      (function
        (lambda ()
          (setq excel (vlax-get-or-create-object "Excel.Application")
                books (vlax-get-property excel 'Workbooks)
                book (vlax-invoke-method books 'Add)
                sheets (vlax-get-property book 'Worksheets))
          (while (< (vlax-get-property sheets 'Count) (1+ (length entries)))
            (vlax-invoke-method sheets 'Add))
          (setq first-sheet (vlax-get-property sheets 'Item 1)
                sheet-objects (list first-sheet))
          (vlax-put-property first-sheet 'Name "Resumo Geral")
          (princ "\n  Resumo Geral...")
          (qhpe35:fill-general-summary first-sheet entries)
          (setq index 2)
          (foreach entry entries
            (setq sheet (vlax-get-property sheets 'Item index)
                  sheet-objects (cons sheet sheet-objects)
                  name (qhpe35:unique-sheet-name (car entry) used-names)
                  used-names (cons name used-names))
            (princ (strcat "\n  Aba " name "..."))
            (vlax-put-property sheet 'Name name)
            (qhpe25:excel-fill-station-sheet sheet
              (strcat "QUANTITATIVOS POR ESTACA - " (car entry))
              (nth 1 entry) (nth 2 entry) (nth 3 entry) (nth 4 entry) nil)
            (setq index (1+ index)))
          (vlax-invoke-method first-sheet 'Activate)
          (setq saved (qhpe35:save-workbook excel book path))
          (vlax-put-property excel 'Visible :vlax-true)
          T))))
  (foreach object (append sheet-objects (list sheets book books excel))
    (if (and object (not (vlax-object-released-p object)))
      (vlax-release-object object)))
  (gc)
  (if (vl-catch-all-error-p result)
    (progn
      (princ (strcat "\nFalha ao exportar para Excel: "
        (vl-catch-all-error-message result)))
      nil)
    (progn
      (if (not saved)
        (princ "\nPlanilha criada no Excel (nao salva). Use Salvar Como para guardar."))
      T))
)

;;; ---------------------------------------------------------- comandos ------

;;; QHPEGERAR e QHPERESUMO: pedem o nome da via e seguem o fluxo da v3.4.
(setq qhpe35:*gerar-original* c:QHPEGERAR
      qhpe35:*resumo-original* c:QHPERESUMO)

(defun c:QHPEGERAR ()
  (princ (strcat "\nQHPE v" qhpe:*version* " - gerar conjunto de quantitativos."))
  (setq qhpe35:*street-name* (qhpe35:prompt-street-name ""))
  (qhpe35:*gerar-original*)
)

(defun c:QHPERESUMO ()
  (setq qhpe35:*street-name* (qhpe35:prompt-street-name ""))
  (qhpe35:*resumo-original*)
)

;;; Exporta todos os conjuntos QHPE do desenho em um unico arquivo Excel.
(defun c:QHPEEXPORTTODAS
  (/ *error* old-error acad doc space old-cmdecho old-osmode old-clayer
    old-street old-wr-current old-wr-default items entries metadata sources
    axis hatch-ss curb-layer data name unnamed path)
  (setq old-error *error*
        acad (vlax-get-acad-object)
        doc (vla-get-ActiveDocument acad)
        space (vla-get-ModelSpace doc)
        old-cmdecho (getvar "CMDECHO")
        old-osmode (getvar "OSMODE")
        old-clayer (getvar "CLAYER")
        old-street qhpe35:*street-name*
        old-wr-current qhpe:*right-width-current*
        old-wr-default qhpe:*right-width-default*)
  (defun *error* (message)
    (qhpe:cleanup-temp)
    (setq qhpe35:*street-name* old-street
          qhpe:*right-width-current* old-wr-current
          qhpe:*right-width-default* old-wr-default)
    (qhpe25:restore-environment doc old-clayer old-cmdecho old-osmode old-error)
    (if (and message
      (not (wcmatch (strcase message) "*CANCEL*,*QUIT*,*EXIT*")))
      (princ (strcat "\nErro QHPEEXPORTTODAS: " message)))
    (princ))
  (vla-StartUndoMark doc)
  (setvar "CMDECHO" 0)
  (setvar "OSMODE" 0)
  (qhpe:prepare-temp-layer doc)

  ;; 1) Coleta os conjuntos (ordem de criacao = nome do grupo).
  (setq items nil)
  (vlax-for group (vla-get-Groups doc)
    (if (wcmatch (strcase (vla-get-Name group))
          (strcat (strcase qhpe25:*group-prefix*) "*"))
      (setq items
        (cons (cons (vla-get-Name group) (qhpe25:group-metadata group)) items))))
  (setq items (vl-sort items (function (lambda (a b) (< (car a) (car b))))))

  (if (null items)
    (*error* "Nenhum conjunto QHPE no desenho. Gere as vias com QHPEGERAR.")
    (progn
      (princ (strcat "\n" (itoa (length items)) " conjunto(s) QHPE encontrado(s)."))
      ;; 2) Recalcula cada via a partir das fontes originais.
      (setq entries nil unnamed 0)
      (foreach item items
        (setq metadata (cdr item))
        (if (and metadata (listp metadata) (qhpe25:metadata-value 'A metadata))
          (progn
            (setq sources (qhpe25:resolve-metadata-sources metadata)
                  axis (nth 0 sources)
                  hatch-ss (nth 1 sources)
                  curb-layer (nth 2 sources)
                  name qhpe35:*street-name*)
            (if (= name "")
              (setq unnamed (1+ unnamed)
                    name (strcat "VIA " (itoa unnamed))))
            (if (or (null axis) (null hatch-ss))
              (princ (strcat "\n  " name ": eixo ou hachuras nao encontrados; ignorada."))
              (progn
                (setq qhpe:*right-width-current*
                        (if (numberp (qhpe25:metadata-value 'WR metadata))
                          (qhpe25:metadata-value 'WR metadata)
                          (qhpe25:metadata-value 'W metadata))
                      qhpe:*right-width-default* qhpe:*right-width-current*)
                (princ (strcat "\n  Calculando " name "..."))
                (setq data
                  (vl-catch-all-apply 'qhpe25:compute-data
                    (list axis hatch-ss curb-layer
                      (qhpe25:metadata-value 'S metadata)
                      (qhpe25:metadata-value 'ST metadata)
                      (qhpe25:metadata-value 'W metadata)
                      (qhpe25:metadata-value 'F metadata)
                      space)))
                (qhpe:cleanup-temp)
                (if (or (vl-catch-all-error-p data) (null data))
                  (princ (strcat "\n  " name ": falha no calculo; ignorada."
                    (if (vl-catch-all-error-p data)
                      (strcat " (" (vl-catch-all-error-message data) ")") "")))
                  (setq entries
                    (append entries
                      (list
                        (list name data
                          (qhpe25:metadata-value 'ST metadata)
                          (qhpe25:metadata-value 'S metadata)
                          (qhpe25:metadata-value 'F metadata)))))))))))
      ;; 3) Exporta.
      (if (null entries)
        (*error* "Nenhuma via pode ser calculada.")
        (progn
          (setq path (qhpe35:ask-excel-path "TODAS_AS_VIAS"))
          (qhpe35:export-all-excel entries path)
          (princ (strcat "\n" (itoa (length entries)) " via(s) exportada(s)."))
          (setq qhpe35:*street-name* old-street
                qhpe:*right-width-current* old-wr-current
                qhpe:*right-width-default* old-wr-default)
          (qhpe25:restore-environment doc old-clayer old-cmdecho old-osmode old-error)))))
  (princ)
)

;;; Define ou altera o nome da via de um conjunto ja gerado (ex.: conjuntos
;;; feitos na v3.4). Depois, QHPEUPDATE redesenha a tabela com o novo nome.
(defun c:QHPENOMEVIA (/ doc selection group metadata current name result)
  (setq doc (vla-get-ActiveDocument (vlax-get-acad-object)))
  (setq selection (entsel "\nSelecione qualquer objeto de um conjunto QHPE: "))
  (cond
    ((null selection)
      (princ "\nNada selecionado."))
    ((null (setq group (qhpe25:find-group-for-entity doc (car selection))))
      (princ "\nO objeto nao pertence a um conjunto QHPE."))
    ((not (listp (setq metadata (qhpe25:group-metadata group))))
      (princ "\nO conjunto nao tem dados QHPE validos."))
    ((null metadata)
      (princ "\nO conjunto nao tem dados QHPE validos."))
    (T
      (setq current (qhpe35:metadata-street-name metadata))
      (princ (strcat "\nNome atual: " (if (= current "") "(sem nome)" current)))
      (setq name (qhpe35:prompt-street-name current))
      (setq metadata
        (if (assoc 'N metadata)
          (subst (cons 'N name) (assoc 'N metadata) metadata)
          (append metadata (list (cons 'N name)))))
      (vla-StartUndoMark doc)
      (setq result
        (vl-catch-all-apply 'vla-put-Description
          (list group (vl-prin1-to-string metadata))))
      (vla-EndUndoMark doc)
      (if (vl-catch-all-error-p result)
        (princ (strcat "\nNao foi possivel gravar o nome: "
          (vl-catch-all-error-message result)))
        (princ "\nNome gravado. Use QHPEUPDATE nesse conjunto para atualizar a tabela de totais."))))
  (princ)
)

(defun qhpe:mostrar-ajuda-comandos ()
  (princ "\n===============================================================")
  (princ "\nQHPE v3.5 - COMANDOS DISPONIVEIS")
  (princ "\n===============================================================")
  (princ "\nQHPEGERAR           - Gera tudo: diagrama, tabelas, resumo/totais e Excel opcional.")
  (princ "\nQHPED                - Gera somente o diagrama linear dos pavimentos e meios-fios.")
  (princ "\nQHPEA                - Calcula e insere somente as tabelas por estaca.")
  (princ "\nQHPERESUMO           - Insere legenda grafica e tabela geral de totais.")
  (princ "\nQHPEUPDATE           - Atualiza um conjunto existente gerado pelo QHPEGERAR.")
  (princ "\nQHPEEXPORT           - Exporta novamente os quantitativos de uma via para o Excel.")
  (princ "\nQHPEEXPORTTODAS      - Exporta todas as vias do desenho em um unico Excel.")
  (princ "\nQHPENOMEVIA          - Define/altera o nome da via de um conjunto existente.")
  (princ "\nQHPEESTILOTABELAS    - Reaplica os estilos QHPE nas tabelas existentes.")
  (princ "\nQHPEESCALATABELAS    - Escala tabelas mantendo o centro visual.")
  (princ "\nQHPECORRIGIRTABELAS  - Alarga a coluna SERVICO e corrige linhas com texto quebrado.")
  (princ "\nQHPEDIAGHATCH        - Diagnostica HATCH sem Area nativa/contorno valido.")
  (princ "\nQHPEAJUDA            - Exibe esta lista de comandos.")
  (princ "\nOBS.: QHPEGERAR/QHPERESUMO pedem o nome da via (clique num texto ou digite).")
  (princ "\nOBS.: ESQUERDA/DIREITA seguem o sentido crescente da polilinha do eixo.")
  (princ "\n===============================================================")
  (princ)
)

(princ "\nQHPE v3.5 carregado. Use QHPEAJUDA para ver os comandos.")
(princ)
