;;@TITULO  Mapa-chave das pranchas
;;@CATEGORIA Pranchas e layouts
;;@ICONE mapa
;;@COMANDO MAPACHAVE
;;@COMANDO MAPACHAVEDYN
;;@VERSAO  1.8
;;@DESC    Gera o mapa-chave a partir dos objetos escolhidos no Model e da area que cada viewport dos layouts
;;@DESC    mostra, com retangulo e identificacao de cada folha, encaixado numa caixa informada.
;;@DESC    MAPACHAVEDYN gera um bloco dinamico com um estado de visibilidade por folha.
;;@USO     Digite MAPACHAVE
;;@USO     Selecione no Model os objetos do mapa
;;@USO     Escolha os viewports e clique os dois cantos da caixa
;; ----------------------------------------------------------------
(vl-load-com)

;; ============================================================
;; MAPACHAVE v1.8
;;
;; Gera mapa-chave a partir de objetos selecionados no Model e
;; das regioes de Model exibidas pelos viewports dos Layouts.
;;
;; Principais mudancas da v1.2:
;;   - Preserva layer/cor dos objetos selecionados no desenho-base.
;;   - Textos de cada viewport ficam paralelos ao maior lado da vista.
;;   - Altura dos textos passa a ser proporcional ao tamanho da viewport.
;;   - A posicao do texto acompanha a rotacao/twist real da viewport.
;;
;; Mantido da v1.1:
;;   - NAO ativa nem entra nos viewports para descobri-los.
;;   - Calcula a janela de cada viewport diretamente pelos dados
;;     da vista (centro, altura, alvo, direcao e twist).
;;   - Evita as mensagens "There are no active Model space viewports".
;;   - Substitui a pergunta abstrata de "largura" por uma CAIXA:
;;     o usuario informa dois cantos da area onde o mapa deve caber.
;;   - Considera os limites dos viewports no enquadramento final,
;;     evitando retangulos ficarem para fora do mapa.
;;   - Ao copiar objetos que nao aceitam SCALE (comum em objetos
;;     inteligentes), tenta gerar uma representacao simplificada
;;     caso a entidade seja uma curva.
;;
;; Comando: MAPACHAVE
;; ============================================================

(setq *MK-LAYER-BASE*  "MAPA_CHAVE_BASE")
(setq *MK-LAYER-VIEW*  "MAPA_CHAVE_VIEWPORT")
(setq *MK-LAYER-TEXTO* "MAPA_CHAVE_TEXTO")
(setq *MK-LAYER-HATCH* "MAPA_CHAVE_HATCH")

;; ------------------------------------------------------------
;; Basicos
;; ------------------------------------------------------------

(defun MK:Doc ()
  (vla-get-ActiveDocument (vlax-get-acad-object))
)

(defun MK:VarList (x)
  (cond
    ((= (type x) 'VARIANT)   (MK:VarList (vlax-variant-value x)))
    ((= (type x) 'SAFEARRAY) (vlax-safearray->list x))
    ((listp x) x)
    (T nil)
  )
)

(defun MK:Pt3 (p)
  (vlax-3d-point
    (car p)
    (cadr p)
    (if (caddr p) (caddr p) 0.0)
  )
)

(defun MK:Fmt2 (n)
  (if (< n 10)
    (strcat "0" (itoa n))
    (itoa n)
  )
)

(defun MK:EnsureLayer (nome / doc layers)
  (setq doc (MK:Doc)
        layers (vla-get-Layers doc))
  (if (not (tblsearch "LAYER" nome))
    (vla-Add layers nome)
  )
  nome
)

;; Preserva as propriedades visuais mais importantes do objeto de origem.
;; Para objetos ByLayer, manter o mesmo layer preserva a cor do layer.
;; Para cores explicitas, copia tambem Color/TrueColor quando disponivel.
(defun MK:CopyVisualProps (src dst / lay col tc)
  (if (and src dst)
    (progn
      (if (and (vlax-property-available-p src 'Layer)
               (vlax-property-available-p dst 'Layer T))
        (progn
          (setq lay (vla-get-Layer src))
          (vl-catch-all-apply 'vla-put-Layer (list dst lay))))

      (if (and (vlax-property-available-p src 'Color)
               (vlax-property-available-p dst 'Color T))
        (progn
          (setq col (vla-get-Color src))
          (vl-catch-all-apply 'vla-put-Color (list dst col))))

      ;; TrueColor so e forçada quando a entidade nao esta ByLayer/ByBlock.
      (if (and col (/= col 256) (/= col 0)
               (vlax-property-available-p src 'TrueColor)
               (vlax-property-available-p dst 'TrueColor T))
        (progn
          (setq tc (vl-catch-all-apply 'vla-get-TrueColor (list src)))
          (if (not (vl-catch-all-error-p tc))
            (vl-catch-all-apply 'vla-put-TrueColor (list dst tc)))))

      (if (and (vlax-property-available-p src 'Lineweight)
               (vlax-property-available-p dst 'Lineweight T))
        (vl-catch-all-apply
          'vla-put-Lineweight
          (list dst (vla-get-Lineweight src))))
    )
  )
  dst
)

(defun MK:CurrentOwner (doc ctab tilemode / layouts lay)
  (if (= tilemode 1)
    (vla-get-ModelSpace doc)
    (progn
      (setq layouts (vla-get-Layouts doc)
            lay (vla-Item layouts ctab))
      (vla-get-Block lay)
    )
  )
)

;; ------------------------------------------------------------
;; Vetores 3D
;; ------------------------------------------------------------

(defun MK:V+ (a b)
  (list (+ (car a) (car b))
        (+ (cadr a) (cadr b))
        (+ (caddr a) (caddr b)))
)

(defun MK:V- (a b)
  (list (- (car a) (car b))
        (- (cadr a) (cadr b))
        (- (caddr a) (caddr b)))
)

(defun MK:V* (s a)
  (list (* s (car a)) (* s (cadr a)) (* s (caddr a)))
)

(defun MK:Dot (a b)
  (+ (* (car a) (car b))
     (* (cadr a) (cadr b))
     (* (caddr a) (caddr b)))
)

(defun MK:Cross3 (a b)
  (list
    (- (* (cadr a) (caddr b)) (* (caddr a) (cadr b)))
    (- (* (caddr a) (car b))  (* (car a) (caddr b)))
    (- (* (car a) (cadr b))   (* (cadr a) (car b)))
  )
)

(defun MK:VLen (a)
  (sqrt (max 0.0 (MK:Dot a a)))
)

(defun MK:Unit (a / l)
  (setq l (MK:VLen a))
  (if (> l 1e-14)
    (MK:V* (/ 1.0 l) a)
  )
)

;; Eixos equivalentes ao plano de vista + aplicacao do twist.
;; Retorna (Xaxis Yaxis Zaxis), todos em WCS.
(defun MK:ViewBasis (dir twist / n ax ay a c s rx ry)
  (setq n (MK:Unit dir))
  (if (not n)
    (setq n '(0.0 0.0 1.0))
  )

  ;; Arbitrary Axis Algorithm usado para obter eixos do plano.
  (if (and (< (abs (car n)) (/ 1.0 64.0))
           (< (abs (cadr n)) (/ 1.0 64.0)))
    (setq ax (MK:Unit (MK:Cross3 '(0.0 1.0 0.0) n)))
    (setq ax (MK:Unit (MK:Cross3 '(0.0 0.0 1.0) n)))
  )
  (if (not ax)
    (setq ax '(1.0 0.0 0.0))
  )
  (setq ay (MK:Unit (MK:Cross3 n ax)))
  (if (not ay)
    (setq ay '(0.0 1.0 0.0))
  )

  ;; DCS -> WCS usa rotacao de -twist.
  (setq a (- twist)
        c (cos a)
        s (sin a)
        rx (MK:V+ (MK:V* c ax) (MK:V* s ay))
        ry (MK:V+ (MK:V* (- s) ax) (MK:V* c ay)))

  (list rx ry n)
)

(defun MK:DcsToWcs (p target basis / xax yax)
  (setq xax (car basis)
        yax (cadr basis))
  (MK:V+
    target
    (MK:V+
      (MK:V* (car p) xax)
      (MK:V* (cadr p) yax)
    )
  )
)

;; ------------------------------------------------------------
;; Bounding boxes
;; ------------------------------------------------------------

(defun MK:GetBBox (obj / mn mx res)
  (setq res
    (vl-catch-all-apply
      'vla-GetBoundingBox
      (list obj 'mn 'mx)
    )
  )
  (if (vl-catch-all-error-p res)
    nil
    (list (MK:VarList mn) (MK:VarList mx))
  )
)

(defun MK:SelectionObjectsAndBoxes (ss / i obj bb objs boxes)
  (setq i 0 objs '() boxes '())
  (repeat (sslength ss)
    (setq obj (vlax-ename->vla-object (ssname ss i))
          bb  (MK:GetBBox obj))
    (if bb
      (progn
        (setq objs  (cons obj objs)
              boxes (cons bb boxes))
      )
    )
    (setq i (1+ i))
  )
  (list (reverse objs) (reverse boxes))
)

(defun MK:OverallBBox (boxes / mn mx b bmn bmx)
  (if boxes
    (progn
      (setq b (car boxes)
            mn (car b)
            mx (cadr b)
            boxes (cdr boxes))
      (foreach b boxes
        (setq bmn (car b)
              bmx (cadr b)
              mn (list
                   (min (car mn)   (car bmn))
                   (min (cadr mn)  (cadr bmn))
                   (min (caddr mn) (caddr bmn)))
              mx (list
                   (max (car mx)   (car bmx))
                   (max (cadr mx)  (cadr bmx))
                   (max (caddr mx) (caddr bmx))))
      )
      (list mn mx)
    )
  )
)

(defun MK:BBoxAddPoint (bb p / mn mx)
  (if bb
    (progn
      (setq mn (car bb) mx (cadr bb))
      (list
        (list (min (car mn) (car p))
              (min (cadr mn) (cadr p))
              (min (caddr mn) (caddr p)))
        (list (max (car mx) (car p))
              (max (cadr mx) (cadr p))
              (max (caddr mx) (caddr p)))
      )
    )
    (list p p)
  )
)

(defun MK:CombinedBBox (boxes vps / bb item p)
  (setq bb (MK:OverallBBox boxes))
  (foreach item vps
    (foreach p (cadddr item)
      (setq bb (MK:BBoxAddPoint bb p))
    )
  )
  bb
)

;; ------------------------------------------------------------
;; Geometria 2D para intersecao viewport x objetos
;; ------------------------------------------------------------

(defun MK:Cross2 (a b c)
  (-
    (* (- (car b) (car a)) (- (cadr c) (cadr a)))
    (* (- (cadr b) (cadr a)) (- (car c) (car a)))
  )
)

(defun MK:PointInBBox (p bb / mn mx eps)
  (setq mn (car bb) mx (cadr bb) eps 1e-8)
  (and
    (>= (car p) (- (car mn) eps))
    (<= (car p) (+ (car mx) eps))
    (>= (cadr p) (- (cadr mn) eps))
    (<= (cadr p) (+ (cadr mx) eps))
  )
)

(defun MK:BBoxCorners (bb / mn mx)
  (setq mn (car bb) mx (cadr bb))
  (list
    (list (car mn) (cadr mn) 0.0)
    (list (car mx) (cadr mn) 0.0)
    (list (car mx) (cadr mx) 0.0)
    (list (car mn) (cadr mx) 0.0)
  )
)

(defun MK:PointInConvex (p poly / eps vals a b rest c pos neg)
  (setq eps 1e-8 vals '() rest poly)
  (while rest
    (setq a (car rest)
          b (if (cdr rest) (cadr rest) (car poly))
          c (MK:Cross2 a b p)
          vals (cons c vals)
          rest (cdr rest))
  )
  (setq pos nil neg nil)
  (foreach c vals
    (cond
      ((> c eps) (setq pos T))
      ((< c (- eps)) (setq neg T))
    )
  )
  (not (and pos neg))
)

(defun MK:Between (a b x eps)
  (and (>= x (- (min a b) eps))
       (<= x (+ (max a b) eps)))
)

(defun MK:OnSegment (a b p / eps)
  (setq eps 1e-8)
  (and
    (< (abs (MK:Cross2 a b p)) eps)
    (MK:Between (car a) (car b) (car p) eps)
    (MK:Between (cadr a) (cadr b) (cadr p) eps)
  )
)

(defun MK:SegmentsIntersect (a b c d / o1 o2 o3 o4 eps)
  (setq eps 1e-8
        o1 (MK:Cross2 a b c)
        o2 (MK:Cross2 a b d)
        o3 (MK:Cross2 c d a)
        o4 (MK:Cross2 c d b))
  (or
    (and (< (* o1 o2) (- eps))
         (< (* o3 o4) (- eps)))
    (MK:OnSegment a b c)
    (MK:OnSegment a b d)
    (MK:OnSegment c d a)
    (MK:OnSegment c d b)
  )
)

(defun MK:EdgesIntersect (p1 p2 / a b c d r1 r2 hit)
  (setq r1 p1 hit nil)
  (while (and r1 (not hit))
    (setq a (car r1)
          b (if (cdr r1) (cadr r1) (car p1))
          r2 p2)
    (while (and r2 (not hit))
      (setq c (car r2)
            d (if (cdr r2) (cadr r2) (car p2)))
      (if (MK:SegmentsIntersect a b c d)
        (setq hit T)
      )
      (setq r2 (cdr r2))
    )
    (setq r1 (cdr r1))
  )
  hit
)

(defun MK:PolyIntersectsBBox (poly bb / bc hit p)
  (setq bc (MK:BBoxCorners bb) hit nil)
  (foreach p bc
    (if (MK:PointInConvex p poly)
      (setq hit T)
    )
  )
  (if (not hit)
    (foreach p poly
      (if (MK:PointInBBox p bb)
        (setq hit T)
      )
    )
  )
  (if (not hit)
    (setq hit (MK:EdgesIntersect poly bc))
  )
  hit
)

(defun MK:ViewportHitsBoxes (corners boxes / hit)
  (setq hit nil)
  (while (and boxes (not hit))
    (if (MK:PolyIntersectsBBox corners (car boxes))
      (setq hit T)
    )
    (setq boxes (cdr boxes))
  )
  hit
)

;; ------------------------------------------------------------
;; Layouts e viewports
;; ------------------------------------------------------------

(defun MK:GetLayouts (/ doc lays lay out)
  (setq doc (MK:Doc)
        lays (vla-get-Layouts doc)
        out '())
  (vlax-for lay lays
    (if (= (vla-get-ModelType lay) :vlax-false)
      (setq out (cons lay out))
    )
  )
  (vl-sort out
    '(lambda (a b)
       (< (vla-get-TabOrder a) (vla-get-TabOrder b))))
)

(defun MK:VPOnP (vp)
  (if (vlax-property-available-p vp 'ViewportOn)
    (= (vla-get-ViewportOn vp) :vlax-true)
    T
  )
)

;; Calcula os 4 cantos da area de MODEL exibida pelo viewport,
;; sem tornar o viewport ativo.
(defun MK:ViewportCorners (vp / en ed vc vh pw ph dir target twist hw hh basis dcs)
  (setq en (vlax-vla-object->ename vp)
        ed (entget en)
        vc (cdr (assoc 12 ed))
        vh (cdr (assoc 45 ed))
        pw (cdr (assoc 40 ed))
        ph (cdr (assoc 41 ed))
        dir (cdr (assoc 16 ed))
        target (cdr (assoc 17 ed))
        twist (cdr (assoc 51 ed)))

  (if (not dir)    (setq dir '(0.0 0.0 1.0)))
  (if (not target) (setq target '(0.0 0.0 0.0)))
  (if (not twist)  (setq twist 0.0))

  (if (and vc vh pw ph
           (> (abs ph) 1e-12)
           (> (abs vh) 1e-12))
    (progn
      (setq hw (* 0.5 vh (/ pw ph))
            hh (* 0.5 vh)
            basis (MK:ViewBasis dir twist)
            dcs (list
                  (list (- (car vc) hw) (- (cadr vc) hh) 0.0)
                  (list (+ (car vc) hw) (- (cadr vc) hh) 0.0)
                  (list (+ (car vc) hw) (+ (cadr vc) hh) 0.0)
                  (list (- (car vc) hw) (+ (cadr vc) hh) 0.0)))
      (mapcar
        '(lambda (p) (MK:DcsToWcs p target basis))
        dcs
      )
    )
  )
)

;; item = (NomeLayout TabOrder ViewportID CantosWCS Handle)
(defun MK:ScanViewports (layouts boxes / all hits lay lname ord blk vp en ed id corners item handle)
  (setq all '() hits '())

  (foreach lay layouts
    (setq lname (vla-get-Name lay)
          ord   (vla-get-TabOrder lay)
          blk   (vla-get-Block lay))

    (vlax-for vp blk
      (if (= (vla-get-ObjectName vp) "AcDbViewport")
        (progn
          (setq en (vlax-vla-object->ename vp)
                ed (entget en)
                id (cdr (assoc 69 ed))
                handle (vla-get-Handle vp))

          ;; ID 1 e o viewport geral do Paper Space.
          (if (and id (> id 1) (MK:VPOnP vp))
            (progn
              (setq corners (MK:ViewportCorners vp))
              (if corners
                (progn
                  (setq item (list lname ord id corners handle)
                        all (cons item all))
                  (if (MK:ViewportHitsBoxes corners boxes)
                    (setq hits (cons item hits))
                  )
                )
              )
            )
          )
        )
      )
    )
  )

  (setq all
    (vl-sort all
      '(lambda (a b)
         (if (= (cadr a) (cadr b))
           (< (caddr a) (caddr b))
           (< (cadr a) (cadr b))))))

  (setq hits
    (vl-sort hits
      '(lambda (a b)
         (if (= (cadr a) (cadr b))
           (< (caddr a) (caddr b))
           (< (cadr a) (cadr b))))))

  (list hits all)
)

;; ------------------------------------------------------------
;; Lista / escolha de viewports
;; ------------------------------------------------------------

(defun MK:PrintVPList (lst total / i item)
  (setq i 1)
  (foreach item lst
    (princ
      (strcat
        "\n  [" (itoa i) "] "
        (car item)
        "  |  viewport " (itoa (caddr item))
        "  |  prancha " (MK:Fmt2 (cadr item)) "/" (MK:Fmt2 total)
      )
    )
    (setq i (1+ i))
  )
  (princ)
)

(defun MK:SplitComma (s / out pos part)
  (setq out '())
  (while (setq pos (vl-string-search "," s))
    (setq part (substr s 1 pos)
          s (substr s (+ pos 2))
          out (cons (vl-string-trim " " part) out))
  )
  (setq out (cons (vl-string-trim " " s) out))
  (reverse out)
)

(defun MK:ParseIndices (s maxn / parts out n p)
  (setq parts (MK:SplitComma s) out '())
  (foreach p parts
    (if (/= p "")
      (progn
        (setq n (atoi p))
        (if (and (>= n 1) (<= n maxn) (not (member n out)))
          (setq out (append out (list n)))
        )
      )
    )
  )
  out
)

(defun MK:PickFromList (lst / s idxs out n)
  (if lst
    (progn
      (setq s
        (getstring T
          "\nDigite os indices desejados separados por virgula <todos>: "))
      (if (= (vl-string-trim " " s) "")
        lst
        (progn
          (setq idxs (MK:ParseIndices s (length lst))
                out '())
          (foreach n idxs
            (setq out (append out (list (nth (1- n) lst)))))
          out
        )
      )
    )
  )
)

;; ------------------------------------------------------------
;; Desenho basico
;; ------------------------------------------------------------

(defun MK:AddLWPolyline (owner pts layer closed / flat sa pl)
  (if (>= (length pts) 2)
    (progn
      (setq flat
        (apply 'append
          (mapcar '(lambda (p) (list (car p) (cadr p))) pts)))
      (setq sa
        (vlax-make-safearray
          vlax-vbDouble
          (cons 0 (1- (length flat)))))
      (vlax-safearray-fill sa flat)
      (setq pl (vla-AddLightWeightPolyline owner sa))
      (if closed (vla-put-Closed pl :vlax-true))
      (vla-put-Layer pl layer)
      pl
    )
  )
)

(defun MK:AddHatchOnPolyline (owner pline layer scale ang / hatch sa)
  (if (and owner pline)
    (progn
      (setq hatch
        (vl-catch-all-apply
          'vla-AddHatch
          (list owner 0 "ANSI31" :vlax-true)))
      (if (not (vl-catch-all-error-p hatch))
        (progn
          (setq sa (vlax-make-safearray vlax-vbObject '(0 . 0)))
          (vlax-safearray-put-element sa 0 pline)
          (vl-catch-all-apply 'vla-AppendOuterLoop (list hatch sa))
          (if (vlax-property-available-p hatch 'Layer T)
            (vl-catch-all-apply 'vla-put-Layer (list hatch layer)))
          (if (vlax-property-available-p hatch 'PatternScale T)
            (vl-catch-all-apply 'vla-put-PatternScale (list hatch (max scale 0.001))))
          (if (vlax-property-available-p hatch 'PatternAngle T)
            (vl-catch-all-apply 'vla-put-PatternAngle (list hatch ang)))
          (if (vlax-property-available-p hatch 'Color T)
            (vl-catch-all-apply 'vla-put-Color (list hatch 8)))
          (vl-catch-all-apply 'vla-Evaluate (list hatch))
          hatch))))
)

(defun MK:AddLine (owner p1 p2 layer / ln)
  (setq ln
    (vl-catch-all-apply
      'vla-AddLine
      (list owner (MK:Pt3 p1) (MK:Pt3 p2))))
  (if (not (vl-catch-all-error-p ln))
    (progn
      (if (vlax-property-available-p ln 'Layer T)
        (vl-catch-all-apply 'vla-put-Layer (list ln layer)))
      ln))
)

(defun MK:SetMTextHeight (mt h)
  (cond
    ((vlax-property-available-p mt 'TextHeight T)
     (vl-catch-all-apply 'vlax-put-property (list mt 'TextHeight h)))
    ((vlax-property-available-p mt 'Height T)
     (vl-catch-all-apply 'vlax-put-property (list mt 'Height h)))
  )
  mt
)

(defun MK:AddMTextCBRot (owner p width h txt layer rot / mt res)
  (setq res
    (vl-catch-all-apply
      'vla-AddMText
      (list owner (MK:Pt3 p) (max width (* h 2.0)) txt)))
  (if (not (vl-catch-all-error-p res))
    (progn
      (setq mt res)
      ;; Bottom Center: o texto cresce para o lado externo da viewport.
      (vl-catch-all-apply 'vla-put-AttachmentPoint (list mt 8))
      (vl-catch-all-apply 'vla-put-InsertionPoint (list mt (MK:Pt3 p)))
      (MK:SetMTextHeight mt h)
      (vl-catch-all-apply 'vla-put-Rotation (list mt rot))
      (vl-catch-all-apply 'vla-put-Layer (list mt layer))
      mt
    )
  )
)

(defun MK:AddMTextCB (owner p width h txt layer)
  (MK:AddMTextCBRot owner p width h txt layer 0.0)
)

;; Mesmo ponto/rotacao da v1.2, mas o texto cresce para o lado oposto.
;; Top Center = 2. Isso produz o "espelhamento" visual desejado sem
;; deslocar toda a identificacao para longe da viewport.
(defun MK:AddMTextCTRot (owner p width h txt layer rot / mt res)
  (setq res
    (vl-catch-all-apply
      'vla-AddMText
      (list owner (MK:Pt3 p) (max width (* h 2.0)) txt)))
  (if (not (vl-catch-all-error-p res))
    (progn
      (setq mt res)
      (vl-catch-all-apply 'vla-put-AttachmentPoint (list mt 2))
      (vl-catch-all-apply 'vla-put-InsertionPoint (list mt (MK:Pt3 p)))
      (MK:SetMTextHeight mt h)
      (vl-catch-all-apply 'vla-put-Rotation (list mt rot))
      (vl-catch-all-apply 'vla-put-Layer (list mt layer))
      mt
    )
  )
)

;; Retorna angulo equivalente mantendo o texto legivel, entre -90 e +90 graus.
(defun MK:ReadableAngle (a / halfpi)
  (setq halfpi (/ pi 2.0))
  (while (> a pi) (setq a (- a (* 2.0 pi))))
  (while (<= a (- pi)) (setq a (+ a (* 2.0 pi))))
  (cond
    ((> a halfpi) (setq a (- a pi)))
    ((< a (- halfpi)) (setq a (+ a pi))))
  a
)

(defun MK:Mid4 (pts / sx sy p)
  (setq sx 0.0 sy 0.0)
  (foreach p pts
    (setq sx (+ sx (car p))
          sy (+ sy (cadr p))))
  (list (/ sx 4.0) (/ sy 4.0) 0.0)
)

;; Geometria do rotulo baseada na propria viewport ja mapeada.
;; Retorna: (posicao angulo altura largura major minor)
(defun MK:ViewportMajorAngle (pts / p0 p1 p3 v1 v2 l1 l2 v)
  (setq p0 (nth 0 pts)
        p1 (nth 1 pts)
        p3 (nth 3 pts)
        v1 (list (- (car p1) (car p0)) (- (cadr p1) (cadr p0)) 0.0)
        v2 (list (- (car p3) (car p0)) (- (cadr p3) (cadr p0)) 0.0)
        l1 (sqrt (+ (* (car v1) (car v1)) (* (cadr v1) (cadr v1))))
        l2 (sqrt (+ (* (car v2) (car v2)) (* (cadr v2) (cadr v2))))
        v (if (>= l1 l2) v1 v2))
  (atan (cadr v) (car v))
)

;; Retorna angulo equivalente mantendo o texto legivel, entre -90 e +90 graus.
;; A funcao MK:ReadableAngle ja existe logo acima.
;;
;; Geometria da v1.2, que visualmente ficou melhor, mas com o lado invertido.
;; Retorna: (posicao angulo altura largura major minor)
(defun MK:ViewportLabelGeom (pts mode / p0 p1 p2 p3 vMajor1 vMajor2 l1 l2 major minor
                                      topVec topLen angle topMid center outVec outLen h offset pos width)
  ;; IMPORTANTE:
  ;; MK:ViewportCorners gera os pontos na ordem semantica do viewport em DCS:
  ;;   p0 = bottom-left
  ;;   p1 = bottom-right
  ;;   p2 = top-right
  ;;   p3 = top-left
  ;;
  ;; Depois de transformar para WCS/mapa-chave essa ordem continua valida.
  ;; Portanto, p3->p2 representa exatamente o sentido horizontal da viewport
  ;; NO LAYOUT, e a aresta p3-p2 continua sendo o "topo" da folha mesmo que
  ;; a geometria real apareca inclinada/vertical no mapa-chave.
  (setq p0 (nth 0 pts)
        p1 (nth 1 pts)
        p2 (nth 2 pts)
        p3 (nth 3 pts)

        vMajor1 (list (- (car p1) (car p0))
                      (- (cadr p1) (cadr p0))
                      0.0)
        vMajor2 (list (- (car p3) (car p0))
                      (- (cadr p3) (cadr p0))
                      0.0)
        l1 (sqrt (+ (* (car vMajor1) (car vMajor1))
                    (* (cadr vMajor1) (cadr vMajor1))))
        l2 (sqrt (+ (* (car vMajor2) (car vMajor2))
                    (* (cadr vMajor2) (cadr vMajor2)))))

  (setq major (max l1 l2)
        minor (min l1 l2))

  (if (< major 1e-12) (setq major 1.0))
  (if (< minor 1e-12) (setq minor (* major 0.20)))

  ;; Topo da viewport NO LAYOUT: top-left -> top-right.
  ;; Nao escolhemos mais o lado por coordenada global X/Y do mapa.
  (setq topVec (list (- (car p2) (car p3))
                     (- (cadr p2) (cadr p3))
                     0.0)
        topLen (sqrt (+ (* (car topVec) (car topVec))
                        (* (cadr topVec) (cadr topVec)))))

  (if (< topLen 1e-12)
    (setq topVec vMajor1
          topLen (max l1 1.0)))

  ;; Mantem exatamente a orientacao que o texto teria olhando a viewport
  ;; no Layout. Nao usamos ReadableAngle aqui, pois somar/subtrair PI faria
  ;; o "topo" virar "baixo" depois da transformacao para o mapa-chave.
  (setq angle (atan (cadr topVec) (car topVec))

        topMid (list (/ (+ (car p3) (car p2)) 2.0)
                     (/ (+ (cadr p3) (cadr p2)) 2.0)
                     0.0)
        center (MK:Mid4 pts)

        ;; Vetor para fora da folha, partindo do centro em direcao ao
        ;; meio da aresta superior do viewport.
        outVec (list (- (car topMid) (car center))
                     (- (cadr topMid) (cadr center))
                     0.0)
        outLen (sqrt (+ (* (car outVec) (car outVec))
                        (* (cadr outVec) (cadr outVec)))))

  (if (< outLen 1e-12)
    (progn
      ;; Normal esquerda de topVec = lado superior no sistema DCS.
      (setq outVec (list (- (/ (cadr topVec) topLen))
                         (/ (car topVec) topLen)
                         0.0)
            outLen 1.0)))

  (setq outVec (list (/ (car outVec) outLen)
                     (/ (cadr outVec) outLen)
                     0.0)

        ;; Mesmas proporcoes que estavam funcionando bem na v1.2/v1.5.
        h (min (* minor (if (= mode "Ambos") 0.145 0.18))
               (* major (if (= mode "Ambos") 0.065 0.08))))

  (setq h (max h (* major 0.012))
        ;; O ponto fica rente ao topo da viewport; Bottom Center faz o
        ;; MTEXT crescer para fora, exatamente como um texto colocado
        ;; acima da viewport no Paper Space.
        offset (* h 0.22)
        pos (list (+ (car topMid) (* (car outVec) offset))
                  (+ (cadr topMid) (* (cadr outVec) offset))
                  0.0)
        width (max (* major 0.92) (* h 4.0)))

  (list pos angle h width major minor)
)

;; ------------------------------------------------------------
;; Texto / numero da prancha
;; ------------------------------------------------------------

(defun MK:FindTextDirect (obj layer / typ txt)
  (setq typ (vla-get-ObjectName obj))
  (if (and
        (member typ '("AcDbText" "AcDbMText" "AcDbAttribute"))
        (= (strcase (vla-get-Layer obj)) (strcase layer)))
    (progn
      (setq txt (vla-get-TextString obj))
      ;; Field bruto: usa fallback pelo TabOrder.
      (if (and txt (not (wcmatch txt "*%<*"))) txt)
    )
  )
)

(defun MK:FindAttrText (br layer / res attrs att found)
  (setq found nil)
  (if (and
        (vlax-property-available-p br 'HasAttributes)
        (= (vla-get-HasAttributes br) :vlax-true))
    (progn
      (setq res (vl-catch-all-apply 'vla-GetAttributes (list br)))
      (if (not (vl-catch-all-error-p res))
        (progn
          (setq attrs (MK:VarList res))
          (foreach att attrs
            (if (not found)
              (setq found (MK:FindTextDirect att layer))))
        )
      )
    )
  )
  found
)

(defun MK:FindLayoutTextByLayer (layout layer / blk obj found typ)
  (setq blk (vla-get-Block layout) found nil)
  (vlax-for obj blk
    (if (not found)
      (progn
        (setq found (MK:FindTextDirect obj layer))
        (if (not found)
          (progn
            (setq typ (vla-get-ObjectName obj))
            (if (= typ "AcDbBlockReference")
              (setq found (MK:FindAttrText obj layer))))
        )
      )
    )
  )
  found
)

(defun MK:SheetNumberText (lname ord total / lay n ttxt)
  (setq lay (vla-Item (vla-get-Layouts (MK:Doc)) lname)
        n (MK:FindLayoutTextByLayer lay "PRANCHA_NUMERO")
        ttxt (MK:FindLayoutTextByLayer lay "PRANCHA_TOTAL"))

  (if (or (not n) (= (vl-string-trim " " n) ""))
    (setq n (MK:Fmt2 ord)))

  (if (or (not ttxt) (= (vl-string-trim " " ttxt) ""))
    (setq ttxt (strcat "/" (MK:Fmt2 total)))
    (if (/= (substr ttxt 1 1) "/")
      (setq ttxt (strcat "/" ttxt))))

  (strcat n ttxt)
)

(defun MK:LabelForVP (item mode total / lname ord num)
  (setq lname (car item)
        ord (cadr item)
        num (MK:SheetNumberText lname ord total))
  (cond
    ((= mode "Nome") lname)
    ((= mode "Ambos") (strcat lname "\\P" num))
    (T num)
  )
)

;; ------------------------------------------------------------
;; Transformacao para a caixa escolhida
;; ------------------------------------------------------------

(defun MK:MapPoint (p srcMin dstOrigin scale)
  (list
    (+ (car dstOrigin) (* scale (- (car p) (car srcMin))))
    (+ (cadr dstOrigin) (* scale (- (cadr p) (cadr srcMin))))
    (if (caddr dstOrigin) (caddr dstOrigin) 0.0)
  )
)

(defun MK:MappedBBox (pts / xs ys)
  (setq xs (mapcar 'car pts)
        ys (mapcar 'cadr pts))
  (list
    (list (apply 'min xs) (apply 'min ys) 0.0)
    (list (apply 'max xs) (apply 'max ys) 0.0)
  )
)

(defun MK:NormalizeBox (p1 p2)
  (list
    (list (min (car p1) (car p2))
          (min (cadr p1) (cadr p2))
          (if (caddr p1) (caddr p1) 0.0))
    (list (max (car p1) (car p2))
          (max (cadr p1) (cadr p2))
          (if (caddr p1) (caddr p1) 0.0))
  )
)

;; ------------------------------------------------------------
;; Copia / representacao simplificada da selecao
;; ------------------------------------------------------------

(defun MK:CopyOne (doc obj owner / sa res arr)
  (setq sa (vlax-make-safearray vlax-vbObject '(0 . 0)))
  (vlax-safearray-put-element sa 0 obj)
  (setq res
    (vl-catch-all-apply
      'vla-CopyObjects
      (list doc sa owner)))
  (if (vl-catch-all-error-p res)
    nil
    (progn
      (setq arr (MK:VarList res))
      (if arr (car arr))))
)

(defun MK:TryCopyTransform (doc obj owner srcBase dstBase scale / cp r1 r2)
  (setq cp (MK:CopyOne doc obj owner))
  (if cp
    (progn
      (setq r1
        (vl-catch-all-apply
          'vla-ScaleEntity
          (list cp (MK:Pt3 srcBase) scale)))
      (if (vl-catch-all-error-p r1)
        (progn
          (vl-catch-all-apply 'vla-Delete (list cp))
          nil)
        (progn
          (setq r2
            (vl-catch-all-apply
              'vla-Move
              (list cp (MK:Pt3 srcBase) (MK:Pt3 dstBase))))
          (if (vl-catch-all-error-p r2)
            (progn
              (vl-catch-all-apply 'vla-Delete (list cp))
              nil)
            (progn
              (MK:CopyVisualProps obj cp)
              cp))))
    )
  )
)

(defun MK:CurveData (obj / ep len r)
  (setq ep (vl-catch-all-apply 'vlax-curve-getEndParam (list obj)))
  (if (vl-catch-all-error-p ep)
    nil
    (progn
      (setq r (vl-catch-all-apply 'vlax-curve-getDistAtParam (list obj ep)))
      (if (vl-catch-all-error-p r)
        nil
        (list ep r))))
)

(defun MK:CurveClosedP (obj / r)
  (setq r (vl-catch-all-apply 'vlax-curve-isClosed (list obj)))
  (if (vl-catch-all-error-p r) nil r)
)

(defun MK:SampleCurveMapped (obj srcMin dstOrigin scale worldStep / dat len n i d p mp pts)
  (setq dat (MK:CurveData obj))
  (if dat
    (progn
      (setq len (cadr dat))
      (if (< len 1e-10)
        nil
        (progn
          (setq n (fix (+ 1.0 (/ len (max worldStep 1e-9)))))
          (if (< n 2) (setq n 2))
          (if (> n 400) (setq n 400))
          (setq i 0 pts '())
          (repeat (1+ n)
            (setq d (* len (/ (float i) (float n)))
                  p (vl-catch-all-apply 'vlax-curve-getPointAtDist (list obj d)))
            (if (not (vl-catch-all-error-p p))
              (progn
                (setq mp (MK:MapPoint p srcMin dstOrigin scale))
                (if (or (not pts)
                        (> (distance mp (car pts)) 1e-9))
                  (setq pts (cons mp pts))))
            )
            (setq i (1+ i))
          )
          (reverse pts)
        )
      )
    )
  )
)

(defun MK:DrawSelection (doc objs owner srcMin dstOrigin scale srcDiag / obj cp pts copied simplified ignored worldStep)
  (setq copied 0 simplified 0 ignored 0
        worldStep (/ (max srcDiag 1.0) 250.0))

  (foreach obj objs
    (setq cp (MK:TryCopyTransform doc obj owner srcMin dstOrigin scale))
    (if cp
      (setq copied (1+ copied))
      (progn
        (setq pts (MK:SampleCurveMapped obj srcMin dstOrigin scale worldStep))
        (if (and pts (>= (length pts) 2))
          (progn
            (setq cp
              (MK:AddLWPolyline
                owner
                pts
                (if (vlax-property-available-p obj 'Layer)
                  (vla-get-Layer obj)
                  *MK-LAYER-BASE*)
                (MK:CurveClosedP obj)))
            (if cp (MK:CopyVisualProps obj cp))
            (setq simplified (1+ simplified)))
          (setq ignored (1+ ignored)))))
  )
  (list copied simplified ignored)
)

;; ------------------------------------------------------------
;; Criacao do mapa
;; ------------------------------------------------------------

(defun MK:ShiftBox (box dx dy / mn mx)
  (setq mn (car box) mx (cadr box))
  (list
    (list (+ (car mn) dx) (+ (cadr mn) dy) (caddr mn))
    (list (+ (car mx) dx) (+ (cadr mx) dy) (caddr mx))))

(defun MK:CreateMap (doc objs boxes vps total owner box mode highlightHandle
                     / srcBB mn mx srcW srcH srcDiag bmn bmx boxW boxH
                       margin h titleReserve availW availH scale drawW drawH
                       dstX dstY dstOrigin drawStats item mapped
                       label lg labelPos labelAng labelH labelW titleY titleH titleW pl hatchScale hatchAng)

  (setq srcBB (MK:CombinedBBox boxes vps)
        mn (car srcBB)
        mx (cadr srcBB)
        srcW (- (car mx) (car mn))
        srcH (- (cadr mx) (cadr mn))
        srcDiag (distance mn mx)
        bmn (car box)
        bmx (cadr box)
        boxW (- (car bmx) (car bmn))
        boxH (- (cadr bmx) (cadr bmn)))

  (if (< srcW 1e-12) (setq srcW 1.0))
  (if (< srcH 1e-12) (setq srcH 1.0))

  (if (or (< boxW 1e-9) (< boxH 1e-9))
    nil
    (progn
      (setq margin (* 0.025 (min boxW boxH))
            h (max (* boxH 0.055) (* boxW 0.012))
            titleReserve (* h (if (= mode "Ambos") 4.4 3.4))
            availW (- boxW (* 2.0 margin))
            availH (- boxH titleReserve (* 2.0 margin)))

      (if (< availH (* boxH 0.25))
        (progn
          (setq h (* boxH 0.04)
                titleReserve (* h (if (= mode "Ambos") 4.4 3.4))
                availH (- boxH titleReserve (* 2.0 margin)))))

      (setq scale (min (/ availW srcW) (/ availH srcH))
            drawW (* scale srcW)
            drawH (* scale srcH)
            dstX (+ (car bmn) margin (/ (- availW drawW) 2.0))
            dstY (+ (cadr bmn) margin (/ (- availH drawH) 2.0))
            dstOrigin (list dstX dstY (caddr bmn)))

      (setq drawStats
        (MK:DrawSelection doc objs owner mn dstOrigin scale srcDiag))

      (foreach item vps
        (setq mapped
          (mapcar '(lambda (p) (MK:MapPoint p mn dstOrigin scale)) (cadddr item)))

        (setq pl (MK:AddLWPolyline owner mapped *MK-LAYER-VIEW* T))

        (if (and highlightHandle pl (= (nth 4 item) highlightHandle))
          (progn
            ;; Metade da escala da v1.4 (1/4 da escala original da v1.3).
            (setq hatchScale
              (* 0.25
                 (max (/ (distance (nth 0 mapped) (nth 1 mapped)) 25.0) 0.05)))

            ;; ANSI31 possui linhas-base a 45 graus. PatternAngle recebe
            ;; apenas a rotacao adicional da longitudinal da viewport;
            ;; portanto a hachura final fica a 45 graus da longitudinal.
            (setq hatchAng
              (MK:ViewportMajorAngle mapped))

            (MK:AddHatchOnPolyline
              owner pl *MK-LAYER-HATCH* hatchScale hatchAng)))

        (setq label (MK:LabelForVP item mode total)
              lg (MK:ViewportLabelGeom mapped mode)
              labelPos (car lg)
              labelAng (cadr lg)
              labelH (caddr lg)
              labelW (cadddr lg))

        (MK:AddMTextCBRot owner labelPos labelW labelH label *MK-LAYER-TEXTO* labelAng))

      ;; Titulo simples, sem codigos de formatacao, para evitar
      ;; aparecer "LMAPA CHAVEl" em algumas configuracoes. A linha
      ;; abaixo do titulo e desenhada separadamente.
      (setq titleY (- (cadr bmx) margin (* h 0.15))
            titleH (* h 1.15)
            titleW (min (* (- boxW (* 2.0 margin)) 0.62)
                        (* titleH 14.0)))

      (MK:AddMTextCB owner
        (list (/ (+ (car bmn) (car bmx)) 2.0) titleY (caddr bmn))
        titleW
        titleH
        "MAPA CHAVE"
        *MK-LAYER-TEXTO*)

      (MK:AddLine owner
        (list (- (/ (+ (car bmn) (car bmx)) 2.0) (* titleW 0.50))
              (- titleY (* titleH 0.22))
              (caddr bmn))
        (list (+ (/ (+ (car bmn) (car bmx)) 2.0) (* titleW 0.50))
              (- titleY (* titleH 0.22))
              (caddr bmn))
        *MK-LAYER-TEXTO*)

      (list scale drawStats)
    )
  )
)

;; ------------------------------------------------------------
;; Restauracao
;; ------------------------------------------------------------

(defun MK:RestoreSpace (doc ctab tilemode)
  (vl-catch-all-apply 'vla-put-MSpace (list doc :vlax-false))
  (if (= tilemode 1)
    (setvar "TILEMODE" 1)
    (progn
      (setvar "TILEMODE" 0)
      (setvar "CTAB" ctab)
      (vl-catch-all-apply 'vla-put-MSpace (list doc :vlax-false)))
  )
)

;; ============================================================
;; COMANDO PRINCIPAL
;; ============================================================

(defun c:MAPACHAVE
  (/ *error* olderr doc origTab origTile layouts total ss data objs boxes
     scan hits allvps op chosen mode hatchIdx hatchItem p1 p2 box owner res undoRes undoStarted stats)

  (vl-load-com)

  (setq doc (MK:Doc)
        origTab (getvar "CTAB")
        origTile (getvar "TILEMODE")
        olderr *error*
        undoStarted nil)

  (defun *error* (msg)
    (if undoStarted
      (vl-catch-all-apply 'vla-EndUndoMark (list doc)))
    (MK:RestoreSpace doc origTab origTile)
    (setq *error* olderr)
    (if (and msg
             (/= msg "Function cancelled")
             (/= msg "quit / exit abort"))
      (princ (strcat "\nErro MAPACHAVE: " msg)))
    (princ)
  )

  (MK:EnsureLayer *MK-LAYER-BASE*)
  (MK:EnsureLayer *MK-LAYER-VIEW*)
  (MK:EnsureLayer *MK-LAYER-TEXTO*)
  (MK:EnsureLayer *MK-LAYER-HATCH*)

  (princ "\nMAPACHAVE v1.8")
  (princ "\nSelecione no Model apenas os objetos que devem aparecer no mapa-chave.")

  ;; Se iniciou em Layout, vai ao Model apenas para a selecao.
  (if (= origTile 0)
    (progn
      (vl-catch-all-apply 'vla-put-MSpace (list doc :vlax-false))
      (setvar "TILEMODE" 1)))

  (princ "\nSelecione os objetos do MAPA CHAVE: ")
  (setq ss (ssget))

  (if (not ss)
    (progn
      (MK:RestoreSpace doc origTab origTile)
      (setq *error* olderr)
      (princ "\nNada selecionado."))
    (progn
      (setq data (MK:SelectionObjectsAndBoxes ss)
            objs (car data)
            boxes (cadr data))

      (setq layouts (MK:GetLayouts)
            total (length layouts))

      (if (or (not objs) (= total 0))
        (progn
          (MK:RestoreSpace doc origTab origTile)
          (setq *error* olderr)
          (if (not objs)
            (princ "\nNao foi possivel ler a geometria selecionada.")
            (princ "\nNao existem Layouts de Paper Space.")))
        (progn
          (princ
            (strcat "\nAnalisando " (itoa total)
                    " layout(s), sem ativar os viewports..."))

          (setq scan (MK:ScanViewports layouts boxes)
                hits (car scan)
                allvps (cadr scan))

          ;; Volta ao espaco de origem antes das proximas perguntas.
          (MK:RestoreSpace doc origTab origTile)

          (cond
            ((not allvps)
             (setq chosen nil)
             (princ "\nNenhum viewport de Paper Space utilizavel foi encontrado."))

            (hits
             (princ
               (strcat "\n\nForam detectados "
                       (itoa (length hits))
                       " viewport(s) cobrindo a selecao:"))
             (MK:PrintVPList hits total)

             (initget "Detectados Escolher Todos")
             (setq op
               (getkword
                 "\nUsar [Detectados/Escolher/Todos] <Detectados>: "))
             (cond
               ((= op "Escolher")
                (princ "\nTodos os viewports disponiveis:")
                (MK:PrintVPList allvps total)
                (setq chosen (MK:PickFromList allvps)))
               ((= op "Todos")
                (setq chosen allvps))
               (T
                (setq chosen hits)))
            )

            (T
             (princ "\nNenhum viewport cruzou automaticamente a selecao.")
             (princ "\nTodos os viewports disponiveis:")
             (MK:PrintVPList allvps total)
             (setq chosen (MK:PickFromList allvps)))
          )

          (if (not chosen)
            (progn
              (setq *error* olderr)
              (princ "\nNenhum viewport selecionado."))
            (progn
              (initget "Numero Nome Ambos")
              (setq mode
                (getkword
                  "\nIdentificacao acima das vistas [Numero/Nome/Ambos] <Numero>: "))
              (if (not mode) (setq mode "Numero"))

              ;; Uma unica hachura. O restante o usuario pode copiar depois.
              (MK:PrintVPList chosen total)
              (initget 6)
              (setq hatchIdx
                (getint
                  "\nQual viewport deseja HACHURAR <1>: "))
              (if (null hatchIdx) (setq hatchIdx 1))
              (if (or (< hatchIdx 1) (> hatchIdx (length chosen)))
                (setq hatchIdx 1))
              (setq hatchItem (nth (1- hatchIdx) chosen))

              (princ "\nAgora defina a CAIXA onde o mapa-chave deve caber.")
              (setq p1 (getpoint "\nPrimeiro canto da caixa do mapa-chave: "))
              (if p1
                (setq p2 (getcorner p1 "\nCanto oposto da caixa do mapa-chave: ")))

              (if (and p1 p2)
                (progn
                  (setq box (MK:NormalizeBox p1 p2)
                        owner (MK:CurrentOwner doc origTab origTile))

                  (setq undoRes
                    (vl-catch-all-apply 'vla-StartUndoMark (list doc)))
                  (if (not (vl-catch-all-error-p undoRes))
                    (setq undoStarted T))

                  (setq res
                    (MK:CreateMap
                      doc objs boxes chosen total owner box mode (nth 4 hatchItem)))

                  (if undoStarted
                    (progn
                      (vl-catch-all-apply 'vla-EndUndoMark (list doc))
                      (setq undoStarted nil)))

                  (vl-catch-all-apply 'vla-Regen (list doc 1))

                  (if res
                    (progn
                      (setq stats (cadr res))
                      (princ
                        (strcat
                          "\nMapa-chave criado com "
                          (itoa (length chosen))
                          " viewport(s), sendo 1 hachurada."))
                      (princ
                        (strcat
                          "\nObjetos-base: "
                          (itoa (car stats)) " copiado(s), "
                          (itoa (cadr stats)) " simplificado(s), "
                          (itoa (caddr stats)) " ignorado(s).")))
                    (princ "\nNao foi possivel criar o mapa-chave."))
                )
                (princ "\nComando cancelado."))
            )
          )
        )
      )
    )
  )

  (MK:RestoreSpace doc origTab origTile)
  (setq *error* olderr)
  (princ)
)



;; ============================================================
;; MAPACHAVEDYN - bloco dinamico com um estado por folha/viewport
;;
;; Estrutura criada:
;;   MAPA_CHAVE_xxx              -> bloco dinamico principal
;;     MAPA_CHAVE_xxx__01        -> mapa completo + folha 01 hachurada
;;     MAPA_CHAVE_xxx__02        -> mapa completo + folha 02 hachurada
;;     ...
;;
;; O bloco principal recebe um parametro de VISIBILIDADE. Cada estado
;; mostra somente um dos sub-blocos acima.
;; ============================================================

(defun MK:UniqueBlockName (base / nome i)
  (setq nome base i 2)
  (while (tblsearch "BLOCK" nome)
    (setq nome (strcat base "_" (itoa i))
          i (1+ i)))
  nome
)

(defun MK:AddBlockDef (doc nome / res)
  (setq res
    (vl-catch-all-apply
      'vla-Add
      (list (vla-get-Blocks doc) (MK:Pt3 '(0.0 0.0 0.0)) nome)))
  (if (vl-catch-all-error-p res) nil res)
)

(defun MK:InsertBlockRef (owner nome pt / res)
  (setq res
    (vl-catch-all-apply
      'vla-InsertBlock
      (list owner (MK:Pt3 pt) nome 1.0 1.0 1.0 0.0)))
  (if (vl-catch-all-error-p res) nil res)
)

(defun MK:ValuePlain (v)
  (cond
    ((= (type v) 'VARIANT) (MK:ValuePlain (vlax-variant-value v)))
    ((= (type v) 'SAFEARRAY) (vlax-safearray->list v))
    (T v))
)

(defun MK:GetDynProps (br / res)
  (setq res
    (vl-catch-all-apply
      'vla-GetDynamicBlockProperties
      (list br)))
  (if (vl-catch-all-error-p res)
    nil
    (MK:VarList res))
)

;; Como o MAPACHAVEDYN adiciona somente um parametro dinamico, o primeiro
;; DynamicBlockReferenceProperty e o parametro de visibilidade.
(defun MK:GetVisibilityProp (br / props)
  (setq props (MK:GetDynProps br))
  (if props (car props))
)

(defun MK:GetDynPropValue (prop / v)
  (if prop
    (progn
      (setq v (vl-catch-all-apply 'vla-get-Value (list prop)))
      (if (vl-catch-all-error-p v)
        nil
        (MK:ValuePlain v))))
)

(defun MK:GetDynAllowedValues (prop / v)
  (if prop
    (progn
      (setq v (vl-catch-all-apply 'vla-get-AllowedValues (list prop)))
      (if (vl-catch-all-error-p v)
        nil
        (MK:ValuePlain v))))
)

(defun MK:SetDynPropValue (prop value / old typ res)
  (if prop
    (progn
      (setq old (vl-catch-all-apply 'vla-get-Value (list prop)))
      (if (vl-catch-all-error-p old)
        nil
        (progn
          (setq typ (if (= (type old) 'VARIANT)
                      (vlax-variant-type old)
                      vlax-vbString))
          (setq res
            (vl-catch-all-apply
              'vla-put-Value
              (list prop (vlax-make-variant value typ))))
          (not (vl-catch-all-error-p res))))))
)

(defun MK:CountOrdInList (ord lst / n item)
  (setq n 0)
  (foreach item lst
    (if (= (cadr item) ord)
      (setq n (1+ n))))
  n
)

(defun MK:StateNames (chosen total / out item base state n)
  (setq out '())
  (foreach item chosen
    (setq base (MK:SheetNumberText (car item) (cadr item) total))
    (if (> (MK:CountOrdInList (cadr item) chosen) 1)
      (setq state (strcat base " - VP" (itoa (caddr item))))
      (setq state base))

    ;; Garante nome unico mesmo se dois layouts tiverem numeracao manual igual.
    (if (member state out)
      (progn
        (setq n 2)
        (while (member (strcat state "-" (itoa n)) out)
          (setq n (1+ n)))
        (setq state (strcat state "-" (itoa n)))))

    (setq out (append out (list state))))
  out
)

(defun MK:ChildNames (parentName n / out i suffix)
  (setq out '() i 1)
  (repeat n
    (setq suffix (if (< i 10) (strcat "0" (itoa i)) (itoa i)))
    (setq out (append out (list (strcat parentName "__" suffix))))
    (setq i (1+ i)))
  out
)

(defun MK:Cmd (args / res)
  (setq res (vl-catch-all-apply 'vl-cmdf args))
  (if (vl-catch-all-error-p res)
    nil
    T)
)

(defun MK:TryCloseBlockEditor ()
  ;; BSAVE/BCLOSE sao tentados separadamente para nao deixar o usuario
  ;; preso no Block Editor caso alguma etapa de autoria falhe.
  (MK:Cmd (list "_.BSAVE"))
  (MK:Cmd (list "_.BCLOSE"))
)

(defun MK:AddVisibilityParameter (parentName paramPt / ok)
  (setq ok T)
  (if (not (MK:Cmd (list "_.-BEDIT" parentName)))
    (setq ok nil))

  (if ok
    (if (not (MK:Cmd (list "_.BPARAMETER" "_Visibility" paramPt)))
      (setq ok nil)))

  (if ok
    (progn
      (if (not (MK:Cmd (list "_.BSAVE"))) (setq ok nil))
      (if (not (MK:Cmd (list "_.BCLOSE"))) (setq ok nil)))
    (MK:TryCloseBlockEditor))

  ok
)

(defun MK:BuildVisibilityStates
  (parentName defaultState stateNames childHandles / ok state handle en firstState)

  (setq ok T)

  (if (not (MK:Cmd (list "_.-BEDIT" parentName)))
    (setq ok nil))

  ;; Exibe objetos invisiveis de forma esmaecida no editor. Isso tambem
  ;; facilita a selecao via handle pelo BVSHOW.
  (if ok
    (vl-catch-all-apply 'setvar (list "BVMODE" 1)))

  ;; Cada estado nasce com tudo oculto; depois mostramos apenas o
  ;; sub-bloco correspondente.
  (if ok
    (progn
      (while (and stateNames childHandles ok)
        (setq state  (car stateNames)
              handle (car childHandles)
              en     (handent handle))

        ;; -BVSTATE > New > nome > Hide All
        (if (not (MK:Cmd (list "_.-BVSTATE" "_New" state "_Hide")))
          (setq ok nil))

        ;; O novo estado vira o atual. Mostra somente o sub-bloco deste estado.
        (if (and ok en)
          (if (not (MK:Cmd (list "_.BVSHOW" en "")))
            (setq ok nil))
          (if ok (setq ok nil)))

        (setq stateNames   (cdr stateNames)
              childHandles (cdr childHandles)))))

  ;; Define o primeiro estado criado como atual e remove o estado
  ;; automatico que nasce junto com o parametro de visibilidade.
  (if ok
    (progn
      (setq firstState (car *MK-DYN-STATES-WORK*))
      (if firstState
        (if (not (MK:Cmd (list "_.-BVSTATE" "_Set" firstState)))
          (setq ok nil)))))

  (if (and ok defaultState firstState (/= defaultState firstState))
    ;; A exclusao pode falhar em alguma versao/localizacao; isso nao
    ;; invalida os estados criados, apenas deixa o estado inicial extra.
    (MK:Cmd (list "_.-BVSTATE" "_Delete" defaultState)))

  (if ok
    (progn
      (if (not (MK:Cmd (list "_.BSAVE"))) (setq ok nil))
      (if (not (MK:Cmd (list "_.BCLOSE"))) (setq ok nil)))
    (MK:TryCloseBlockEditor))

  ok
)

(defun MK:CreateDynamicMapBlock
  (doc objs boxes chosen total owner box mode requestedName
   / bmn bmx w h localBox parentName parentDef childNames stateNames
     item childName childDef childRef childRefs childHandles parentRef
     paramPt prop defaultState ok i allowed)

  (setq bmn (car box)
        bmx (cadr box)
        w (- (car bmx) (car bmn))
        h (- (cadr bmx) (cadr bmn)))

  (if (or (< w 1e-9) (< h 1e-9))
    nil
    (progn
      (setq parentName (MK:UniqueBlockName requestedName)
            parentDef  (MK:AddBlockDef doc parentName)
            localBox   (list '(0.0 0.0 0.0) (list w h 0.0))
            childNames (MK:ChildNames parentName (length chosen))
            stateNames (MK:StateNames chosen total)
            childRefs '()
            childHandles '())

      (if (not parentDef)
        nil
        (progn
          ;; Cria um bloco-filho completo por folha/viewport. Em cada um,
          ;; somente a sua propria folha recebe hachura.
          (setq i 0 ok T)
          (while (and (< i (length chosen)) ok)
            (setq item      (nth i chosen)
                  childName (nth i childNames)
                  childDef  (MK:AddBlockDef doc childName))

            (if (not childDef)
              (setq ok nil)
              (progn
                (if (not
                      (MK:CreateMap
                        doc objs boxes chosen total childDef localBox mode (nth 4 item)))
                  (setq ok nil)
                  (progn
                    (setq childRef
                      (MK:InsertBlockRef parentDef childName '(0.0 0.0 0.0)))
                    (if childRef
                      (progn
                        (setq childRefs
                          (append childRefs (list childRef)))
                        (setq childHandles
                          (append childHandles (list (vla-get-Handle childRef)))))
                      (setq ok nil)))))
            )
            (setq i (1+ i)))

          (if (not ok)
            nil
            (progn
              ;; Insere o bloco principal no ponto correspondente ao canto
              ;; inferior esquerdo da caixa escolhida pelo usuario.
              (setq parentRef
                (MK:InsertBlockRef owner parentName bmn))

              (if (not parentRef)
                nil
                (progn
                  ;; O grip de visibilidade fica discretamente proximo do
                  ;; canto superior direito do mapa.
                  (setq paramPt (list (* w 0.97) (* h 0.93) 0.0))

                  ;; 1) adiciona o parametro; 2) le o nome do estado inicial;
                  ;; 3) reabre o editor e monta todos os estados.
                  (if (not (MK:AddVisibilityParameter parentName paramPt))
                    (list parentRef parentName nil stateNames childNames)
                    (progn
                      (vl-catch-all-apply 'vla-Update (list parentRef))
                      (setq prop (MK:GetVisibilityProp parentRef)
                            defaultState (MK:GetDynPropValue prop))

                      ;; variavel temporaria usada para preservar o primeiro
                      ;; nome enquanto a lista e consumida na rotina abaixo.
                      (setq *MK-DYN-STATES-WORK* stateNames)

                      (setq ok
                        (MK:BuildVisibilityStates
                          parentName defaultState stateNames childHandles))

                      ;; Atualiza a referencia e tenta deixar o primeiro estado ativo.
                      (vl-catch-all-apply 'vla-Update (list parentRef))
                      (setq prop (MK:GetVisibilityProp parentRef))
                      (if (and prop stateNames)
                        (MK:SetDynPropValue prop (car stateNames)))
                      (vl-catch-all-apply 'vla-Update (list parentRef))

                      (setq allowed (MK:GetDynAllowedValues prop))
                      (list parentRef parentName ok stateNames childNames allowed)))))))))))
)

;; ============================================================
;; COMANDO: MAPACHAVEDYN
;; ============================================================

(defun c:MAPACHAVEDYN
  (/ *error* olderr doc origTab origTile layouts total ss data objs boxes
     scan hits allvps op chosen mode p1 p2 box owner undoRes undoStarted
     baseName dynRes parentRef parentName dynOK states allowed)

  (vl-load-com)

  (setq doc (MK:Doc)
        origTab (getvar "CTAB")
        origTile (getvar "TILEMODE")
        olderr *error*
        undoStarted nil)

  (defun *error* (msg)
    (if undoStarted
      (vl-catch-all-apply 'vla-EndUndoMark (list doc)))
    (MK:RestoreSpace doc origTab origTile)
    (setq *error* olderr)
    (if (and msg
             (/= msg "Function cancelled")
             (/= msg "quit / exit abort"))
      (princ (strcat "\nErro MAPACHAVEDYN: " msg)))
    (princ)
  )

  (MK:EnsureLayer *MK-LAYER-BASE*)
  (MK:EnsureLayer *MK-LAYER-VIEW*)
  (MK:EnsureLayer *MK-LAYER-TEXTO*)
  (MK:EnsureLayer *MK-LAYER-HATCH*)

  (princ "\nMAPACHAVEDYN v1.8")
  (princ "\nGera um bloco dinamico com um estado de visibilidade por folha/viewport.")
  (princ "\nSelecione no Model os objetos que devem aparecer no mapa-chave.")

  (if (= origTile 0)
    (progn
      (vl-catch-all-apply 'vla-put-MSpace (list doc :vlax-false))
      (setvar "TILEMODE" 1)))

  (princ "\nSelecione os objetos do MAPA CHAVE: ")
  (setq ss (ssget))

  (if (not ss)
    (progn
      (MK:RestoreSpace doc origTab origTile)
      (setq *error* olderr)
      (princ "\nNada selecionado."))
    (progn
      (setq data (MK:SelectionObjectsAndBoxes ss)
            objs (car data)
            boxes (cadr data)
            layouts (MK:GetLayouts)
            total (length layouts))

      (if (or (not objs) (= total 0))
        (progn
          (MK:RestoreSpace doc origTab origTile)
          (setq *error* olderr)
          (princ "\nNao foi possivel preparar o mapa-chave."))
        (progn
          (setq scan (MK:ScanViewports layouts boxes)
                hits (car scan)
                allvps (cadr scan))

          (MK:RestoreSpace doc origTab origTile)

          (cond
            ((not allvps)
             (setq chosen nil)
             (princ "\nNenhum viewport utilizavel foi encontrado."))

            (hits
             (princ
               (strcat "\n\nForam detectados "
                       (itoa (length hits))
                       " viewport(s) cobrindo a selecao:"))
             (MK:PrintVPList hits total)
             (initget "Detectados Escolher Todos")
             (setq op
               (getkword
                 "\nUsar [Detectados/Escolher/Todos] <Detectados>: "))
             (cond
               ((= op "Escolher")
                (princ "\nTodos os viewports disponiveis:")
                (MK:PrintVPList allvps total)
                (setq chosen (MK:PickFromList allvps)))
               ((= op "Todos") (setq chosen allvps))
               (T (setq chosen hits))))

            (T
             (princ "\nNenhum viewport cruzou automaticamente a selecao.")
             (MK:PrintVPList allvps total)
             (setq chosen (MK:PickFromList allvps))))

          (if (not chosen)
            (princ "\nNenhum viewport selecionado.")
            (progn
              (initget "Numero Nome Ambos")
              (setq mode
                (getkword
                  "\nIdentificacao acima das vistas [Numero/Nome/Ambos] <Numero>: "))
              (if (not mode) (setq mode "Numero"))

              (setq baseName
                (getstring T "\nNome do bloco dinamico <MAPA_CHAVE>: "))
              (if (= (vl-string-trim " " baseName) "")
                (setq baseName "MAPA_CHAVE"))

              (princ "\nDefina a CAIXA onde o mapa-chave deve caber.")
              (setq p1 (getpoint "\nPrimeiro canto da caixa: "))
              (if p1
                (setq p2 (getcorner p1 "\nCanto oposto da caixa: ")))

              (if (and p1 p2)
                (progn
                  (setq box (MK:NormalizeBox p1 p2)
                        owner (MK:CurrentOwner doc origTab origTile))

                  (setq undoRes
                    (vl-catch-all-apply 'vla-StartUndoMark (list doc)))
                  (if (not (vl-catch-all-error-p undoRes))
                    (setq undoStarted T))

                  (princ
                    (strcat
                      "\nCriando " (itoa (length chosen))
                      " variacao(oes) do mapa e montando os estados..."))

                  (setq dynRes
                    (MK:CreateDynamicMapBlock
                      doc objs boxes chosen total owner box mode baseName))

                  (if undoStarted
                    (progn
                      (vl-catch-all-apply 'vla-EndUndoMark (list doc))
                      (setq undoStarted nil)))

                  (vl-catch-all-apply 'vla-Regen (list doc 1))

                  (if dynRes
                    (progn
                      (setq parentRef  (nth 0 dynRes)
                            parentName (nth 1 dynRes)
                            dynOK      (nth 2 dynRes)
                            states     (nth 3 dynRes)
                            allowed    (nth 5 dynRes))

                      (princ
                        (strcat
                          "\nBloco criado: " parentName
                          " | " (itoa (length states)) " folha(s)."))

                      (if dynOK
                        (progn
                          (princ "\nBloco dinamico criado com estados de visibilidade:")
                          (foreach op states
                            (princ (strcat "\n  - " op)))
                          (princ
                            "\nCopie o bloco normalmente e troque a folha pelo grip/propriedade de Visibilidade."))
                        (progn
                          (princ
                            "\nOs sub-blocos foram criados, mas o AutoCAD nao concluiu a autoria dinamica automaticamente.")
                          (princ
                            "\nO bloco principal ficou no desenho para diagnostico.")))
                    )
                    (princ "\nNao foi possivel criar o bloco do mapa-chave."))
                )
                (princ "\nComando cancelado."))))))))

  (MK:RestoreSpace doc origTab origTile)
  (setq *error* olderr)
  (princ)
)

(princ "\nMAPACHAVE v1.8 carregado.")
(princ "\nComandos: MAPACHAVE (normal) / MAPACHAVEDYN (bloco dinamico)")
(princ)
