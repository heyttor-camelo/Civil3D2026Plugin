;;@TITULO  Join aprimorado
;;@CATEGORIA Polilinhas e geometria
;;@ICONE juntar
;;@COMANDO JPLUS
;;@COMANDO JPLU
;;@COMANDO JPLUSA
;;@COMANDO JPLUSX
;;@COMANDO JPLUSL
;;@COMANDO JPLUSFALHAS
;;@COMANDO JPLUSRESET
;;@COMANDO JPLUSHELP
;;@VERSAO  2.4.2
;;@DESC    JOIN que corrige o que impede a uniao: aproxima pontas dentro de uma tolerancia, trata conflito de Z
;;@DESC    e forma cadeias de LINE, ARC, LWPOLYLINE e POLYLINE 2D/3D, tudo num unico UNDO.
;;@DESC    JPLUS e o modo automatico; JPLUSA pergunta antes de corrigir; JPLUSX e o modo agressivo;
;;@DESC    JPLUSL liga trechos separados criando LINEs; JPLUSFALHAS mostra os pares que o AutoCAD recusou.
;;@USO     Digite JPLUS (ou JPLU) e selecione os objetos
;;@USO     Informe a tolerancia XY
;;@USO     JPLUSHELP mostra todos os modos
;; ----------------------------------------------------------------
;;; ================================================================
;;; JPLUS.LSP - v2.0
;;; Join aprimorado para AutoCAD / Civil 3D
;;;
;;; Comandos:
;;;   JPLUS  - corrige automaticamente, sem mensagens por problema
;;;   JPLUSA - diagnostica cada problema e pergunta antes de corrigir
;;;   JPLUSL - liga trechos separados criando LINEs entre extremidades
;;;
;;; Regras principais:
;;;   1) ARC prevalece sobre segmento reto.
;;;   2) Entre tipos iguais, o segmento maior prevalece.
;;;   3) Em conflito de Z, o usuário escolhe se prevalece a cota
;;;      Superior ou Inferior, uma vez por execução.
;;;   4) Tolerância XY é informada a cada execução.
;;;   5) Toda a operação fica em um único grupo de UNDO.
;;;
;;; Tipos suportados:
;;;   LINE, ARC, LWPOLYLINE, POLYLINE 2D/3D.
;;;
;;; v2.0:
;;;   - Intersecoes internas passam a funcionar como FILLET R=0 real.
;;;   - Ao dividir uma polilinha no no, a porcao com MENOS VERTICES e
;;;     descartada; comprimento e usado somente como desempate.
;;;   - Endpoint contra interior tambem poda o receptor para formar uma
;;;     cadeia univel, em vez de deixar uma conexao em T.
;;;   - Apos os cortes, endpoints sobreviventes sao normalizados no mesmo no
;;;     e o JOIN final continua sendo executado par a par.
;;;
;;; Observação importante:
;;;   Este comando tem objetivo de FORMAR CADEIAS. Em endpoint contra o
;;;   interior de outro objeto, o receptor pode ser dividido no no e o lado
;;;   topologicamente menor (menos vertices) e removido para permitir JOIN.
;;; ================================================================

(vl-load-com)

(setq *JP-TOL* 0.050)
(setq *JP-EPS* 1.0e-8)
(setq *JP-MAXPASS* 1000)
(setq *JP-BRIDGE-MAX* 1.000)

(defun jp:2d (p) (list (car p) (cadr p) 0.0))
(defun jp:dist2d (a b) (distance (jp:2d a) (jp:2d b)))
(defun jp:z (p) (if (caddr p) (caddr p) 0.0))
(defun jp:ptz (p z) (list (car p) (cadr p) z))
(defun jp:mid (a b) (mapcar '(lambda (x y) (/ (+ x y) 2.0)) a b))
(defun jp:abs (x) (if (< x 0.0) (- x) x))
(defun jp:min2 (a b) (if (< a b) a b))
(defun jp:max2 (a b) (if (> a b) a b))

;;; Comparacao lexicografica de strings sem depender de STRCMP,
;;; que nao existe no AutoLISP/Visual LISP do AutoCAD.
(defun jp:str-less-p (a b / la lb n i ca cb done result)
  (setq la (strlen a)
        lb (strlen b)
        n  (min la lb)
        i  1
        done nil
        result nil)
  (while (and (<= i n) (not done))
    (setq ca (ascii (substr a i 1))
          cb (ascii (substr b i 1)))
    (cond
      ((< ca cb) (setq result T   done T))
      ((> ca cb) (setq result nil done T))
      (T (setq i (1+ i)))))
  (if done result (< la lb)))

(defun jp:etype (e) (cdr (assoc 0 (entget e))))
(defun jp:alive-p (e) (and e (entget e)))

(defun jp:curve-p (e / typ)
  (and (jp:alive-p e)
       (member (setq typ (jp:etype e)) '("LINE" "ARC" "LWPOLYLINE" "POLYLINE"))))

(defun jp:closed-p (e)
  (if (jp:curve-p e) (vlax-curve-isClosed e) nil))

(defun jp:cross2 (a b c)
  (- (* (- (car b) (car a)) (- (cadr c) (cadr a)))
     (* (- (cadr b) (cadr a)) (- (car c) (car a)))))

(defun jp:seg-kind (p0 pm p1 / chord h)
  (setq chord (max (jp:dist2d p0 p1) 1.0e-12))
  (setq h (/ (jp:abs (jp:cross2 p0 p1 pm)) chord))
  (if (> h 1.0e-7) "ARC" "LINE"))

(defun jp:get-terminal-data (e side / typ sp ep a b midp p0 p1 pm d0 d1 len kind)
  ;; Retorna alist com dados do segmento adjacente ao endpoint.
  ;; side = "START" ou "END".
  (if (and (jp:curve-p e) (not (jp:closed-p e)))
    (progn
      (setq typ (jp:etype e)
            sp  (vlax-curve-getStartParam e)
            ep  (vlax-curve-getEndParam e))

      (cond
        ((member typ '("LINE" "ARC"))
         (setq a sp b ep))
        ((= side "START")
         (setq a sp b (min ep (+ sp 1.0))))
        (T
         (setq a (max sp (- ep 1.0)) b ep)))

      (setq midp (/ (+ a b) 2.0)
            p0   (vlax-curve-getPointAtParam e a)
            p1   (vlax-curve-getPointAtParam e b)
            pm   (vlax-curve-getPointAtParam e midp)
            d0   (vlax-curve-getDistAtParam e a)
            d1   (vlax-curve-getDistAtParam e b)
            len  (jp:abs (- d1 d0))
            kind (jp:seg-kind p0 pm p1))

      (if (= side "START")
        (list (cons 'ent e) (cons 'side side) (cons 'pt p0)
              (cons 'inner p1) (cons 'len len) (cons 'kind kind)
              (cons 'typ typ))
        (list (cons 'ent e) (cons 'side side) (cons 'pt p1)
              (cons 'inner p0) (cons 'len len) (cons 'kind kind)
              (cons 'typ typ))))))

(defun jp:get-endpoints (ents / out e d)
  (setq out '())
  (foreach e ents
    (if (and (jp:curve-p e) (not (jp:closed-p e)))
      (progn
        (setq d (jp:get-terminal-data e "START"))
        (if d (setq out (cons d out)))
        (setq d (jp:get-terminal-data e "END"))
        (if d (setq out (cons d out))))))
  (reverse out))

(defun jp:assocv (k a) (cdr (assoc k a)))

(defun jp:handle (e)
  (if (jp:alive-p e) (vla-get-Handle (vlax-ename->vla-object e)) "?"))

(defun jp:end-key (d)
  (strcat (jp:handle (jp:assocv 'ent d)) ":" (jp:assocv 'side d)))

(defun jp:pair-key (prefix a b / ka kb)
  (setq ka (jp:end-key a) kb (jp:end-key b))
  (if (jp:str-less-p ka kb)
    (strcat prefix ":" ka "|" kb)
    (strcat prefix ":" kb "|" ka)))

(defun jp:entpair-key (prefix ea eb / a b)
  (setq a (jp:handle ea) b (jp:handle eb))
  (if (jp:str-less-p a b)
    (strcat prefix ":" a "|" b)
    (strcat prefix ":" b "|" a)))

(defun jp:skipped-p (key) (member key *JP-SKIP*))
(defun jp:add-skip (key)
  (if (not (member key *JP-SKIP*)) (setq *JP-SKIP* (cons key *JP-SKIP*))))

(defun jp:choose-mover (a b / ka kb la lb)
  ;; Retorna a estrutura que deve ser modificada.
  (setq ka (jp:assocv 'kind a)
        kb (jp:assocv 'kind b)
        la (jp:assocv 'len a)
        lb (jp:assocv 'len b))
  (cond
    ((and (= ka "LINE") (= kb "ARC")) a)
    ((and (= ka "ARC") (= kb "LINE")) b)
    ((< la lb) a)
    ((> la lb) b)
    (T b)))

(defun jp:desired-z (z1 z2 zmode)
  (if (= zmode "Superior") (max z1 z2) (min z1 z2)))

;;; --------------------- edição de vértices -------------------------

(defun jp:replace-nth-code (lst code nth newpair / i out x)
  (setq i 0 out '())
  (foreach x lst
    (if (= (car x) code)
      (progn
        (if (= i nth)
          (setq out (cons newpair out))
          (setq out (cons x out)))
        (setq i (1+ i)))
      (setq out (cons x out))))
  (reverse out))

(defun jp:count-code (lst code / n x)
  (setq n 0)
  (foreach x lst (if (= (car x) code) (setq n (1+ n))))
  n)

(defun jp:set-lw-end (e side p / ed n idx ocs z obj)
  (setq ed (entget e)
        n  (jp:count-code ed 10)
        idx (if (= side "START") 0 (1- n))
        ocs (trans p 0 e)
        z (jp:z p)
        obj (vlax-ename->vla-object e))
  (setq ed (jp:replace-nth-code ed 10 idx (cons 10 (list (car ocs) (cadr ocs)))))
  (entmod ed)
  (vla-put-Elevation obj z)
  (vla-Update obj)
  (entupd e)
  T)

(defun jp:first-last-vertex (e / v first last)
  (setq v (entnext e) first nil last nil)
  (while (and v (/= (jp:etype v) "SEQEND"))
    (if (= (jp:etype v) "VERTEX")
      (progn
        (if (null first) (setq first v))
        (setq last v)))
    (setq v (entnext v)))
  (list first last))

(defun jp:set-oldpoly-end (e side p / vlst v ed ocs)
  (setq vlst (jp:first-last-vertex e)
        v (if (= side "START") (car vlst) (cadr vlst)))
  (if v
    (progn
      (setq ed (entget v)
            ocs (trans p 0 v))
      (setq ed (subst (cons 10 ocs) (assoc 10 ed) ed))
      (entmod ed)
      (entupd e)
      T)
    nil))

(defun jp:normang (a)
  (while (< a 0.0) (setq a (+ a (* 2.0 pi))))
  (while (>= a (* 2.0 pi)) (setq a (- a (* 2.0 pi))))
  a)

(defun jp:ccw-delta (a b)
  (setq a (jp:normang a) b (jp:normang b))
  (if (>= b a) (- b a) (+ (- (* 2.0 pi) a) b)))

(defun jp:between-ccw-p (a m b / ab am)
  (setq ab (jp:ccw-delta a b)
        am (jp:ccw-delta a m))
  (<= am (+ ab 1.0e-9)))

(defun jp:circumcenter2d (a b c / ax ay bx by cx cy d ux uy)
  (setq ax (car a) ay (cadr a)
        bx (car b) by (cadr b)
        cx (car c) cy (cadr c)
        d (* 2.0 (+ (* ax (- by cy)) (* bx (- cy ay)) (* cx (- ay by)))))
  (if (> (jp:abs d) 1.0e-12)
    (progn
      (setq ux (/ (+ (* (+ (* ax ax) (* ay ay)) (- by cy))
                     (* (+ (* bx bx) (* by by)) (- cy ay))
                     (* (+ (* cx cx) (* cy cy)) (- ay by))) d)
            uy (/ (+ (* (+ (* ax ax) (* ay ay)) (- cx bx))
                     (* (+ (* bx bx) (* by by)) (- ax cx))
                     (* (+ (* cx cx) (* cy cy)) (- bx ax))) d))
      (list ux uy 0.0))
    nil))

(defun jp:set-arc-end (e side p / obj sp ep midp oldS oldE mid newS newE cc r aS aM aE z)
  ;; Reconstrói o arco por novo endpoint + midpoint original + endpoint oposto.
  (setq obj (vlax-ename->vla-object e)
        sp  (vlax-curve-getStartParam e)
        ep  (vlax-curve-getEndParam e)
        midp (/ (+ sp ep) 2.0)
        oldS (vlax-curve-getStartPoint e)
        oldE (vlax-curve-getEndPoint e)
        mid  (vlax-curve-getPointAtParam e midp)
        z    (jp:z p))
  (setq oldS (jp:ptz oldS z)
        oldE (jp:ptz oldE z)
        mid  (jp:ptz mid z))
  (if (= side "START")
    (setq newS p newE oldE)
    (setq newS oldS newE p))
  (setq cc (jp:circumcenter2d newS mid newE))
  (if cc
    (progn
      (setq cc (jp:ptz cc z)
            r  (jp:dist2d cc newS)
            aS (angle cc newS)
            aM (angle cc mid)
            aE (angle cc newE))
      (vla-put-Center obj (vlax-3d-point cc))
      (vla-put-Radius obj r)
      ;; AutoCAD arcos são anti-horários. Se o midpoint não estiver no arco
      ;; CCW start->end, inverte-se a orientação geométrica.
      (if (jp:between-ccw-p aS aM aE)
        (progn (vla-put-StartAngle obj aS) (vla-put-EndAngle obj aE))
        (progn (vla-put-StartAngle obj aE) (vla-put-EndAngle obj aS)))
      (vla-Update obj)
      T)
    nil))

(defun jp:set-line-end (e side p / obj)
  (setq obj (vlax-ename->vla-object e))
  (if (= side "START")
    (vla-put-StartPoint obj (vlax-3d-point p))
    (vla-put-EndPoint obj (vlax-3d-point p)))
  (vla-Update obj)
  T)

(defun jp:set-endpoint (d p / e side typ)
  (setq e (jp:assocv 'ent d)
        side (jp:assocv 'side d)
        typ (jp:etype e))
  (cond
    ((= typ "LINE")       (jp:set-line-end e side p))
    ((= typ "ARC")        (jp:set-arc-end e side p))
    ((= typ "LWPOLYLINE") (jp:set-lw-end e side p))
    ((= typ "POLYLINE")   (jp:set-oldpoly-end e side p))
    (T nil)))

;;; ------------------------- interface ------------------------------

(defun jp:mark (p tol / s)
  (setq s (max (* tol 0.35) 0.05))
  (redraw)
  (grdraw (list (- (car p) s) (- (cadr p) s) (jp:z p))
          (list (+ (car p) s) (+ (cadr p) s) (jp:z p)) 1 1)
  (grdraw (list (- (car p) s) (+ (cadr p) s) (jp:z p))
          (list (+ (car p) s) (- (cadr p) s) (jp:z p)) 1 1))

(defun jp:zoom-problem (p tol)
  (command "_.ZOOM" "_C" p (max (* tol 20.0) 1.0)))

(defun jp:ask-action (title a b p tol / ans)
  (princ (strcat "\n\n[JPLUS] " title))
  (if a
    (princ
      (strcat "\n  A: " (jp:assocv 'typ a)
              " / " (jp:assocv 'kind a)
              " / seg=" (rtos (jp:assocv 'len a) 2 4))))
  (if b
    (princ
      (strcat "\n  B: " (jp:assocv 'typ b)
              " / " (jp:assocv 'kind b)
              " / seg=" (rtos (jp:assocv 'len b) 2 4))))
  (jp:mark p tol)
  (setq ans nil)
  (while (null ans)
    (initget "Corrigir Ignorar Zoom Sair")
    (setq ans (getkword "\n  [Corrigir/Ignorar/Zoom/Sair] <Corrigir>: "))
    (if (null ans) (setq ans "Corrigir"))
    (if (= ans "Zoom")
      (progn
        (jp:zoom-problem p tol)
        (setq ans nil)
        (jp:mark p tol))))
  (redraw)
  ans)

(defun jp:confirm-p (mode title a b p tol / ans)
  (if (= mode "AUTO")
    T
    (progn
      (setq ans (jp:ask-action title a b p tol))
      (cond ((= ans "Corrigir") T)
            ((= ans "Sair") (setq *JP-STOP* T) nil)
            (T nil)))))

;;; ---------------- FILLET 0 para pontas -------------------------

(defun jp:inf-line-intersection2d (p1 p2 p3 p4 / x1 y1 x2 y2 x3 y3 x4 y4 den px py)
  ;; Intersecao das RETAS infinitas p1-p2 e p3-p4 em XY.
  (setq x1 (car p1) y1 (cadr p1)
        x2 (car p2) y2 (cadr p2)
        x3 (car p3) y3 (cadr p3)
        x4 (car p4) y4 (cadr p4)
        den (- (* (- x1 x2) (- y3 y4))
               (* (- y1 y2) (- x3 x4))))
  (if (> (jp:abs den) 1.0e-12)
    (progn
      (setq px (/ (- (* (- (* x1 y2) (* y1 x2)) (- x3 x4))
                       (* (- x1 x2) (- (* x3 y4) (* y3 x4)))) den)
            py (/ (- (* (- (* x1 y2) (* y1 x2)) (- y3 y4))
                       (* (- y1 y2) (- (* x3 y4) (* y3 x4)))) den))
      (list px py 0.0))))

(defun jp:terminal-fillet-point (a b tol / ip pa pb)
  ;; Para duas pontas RETAS nao paralelas, usa a intersecao matematica
  ;; das direcoes, reproduzindo FILLET R=0. So aceita se a intersecao
  ;; estiver dentro da tolerancia de AMBAS as pontas.
  (if (and (= (jp:assocv 'kind a) "LINE")
           (= (jp:assocv 'kind b) "LINE"))
    (progn
      (setq pa (jp:assocv 'pt a)
            pb (jp:assocv 'pt b)
            ip (jp:inf-line-intersection2d
                 (jp:assocv 'inner a) pa
                 (jp:assocv 'inner b) pb))
      (if (and ip
               (<= (jp:dist2d pa ip) tol)
               (<= (jp:dist2d pb ip) tol))
        ip))))

;;; ---------------- endpoint x endpoint ----------------------------

(defun jp:find-endpair (ents tol / ends best bd i j a b da db dxy dz key)
  (setq ends (jp:get-endpoints ents)
        best nil bd 1.0e99 i 0)
  (while (< i (length ends))
    (setq a (nth i ends) j (1+ i))
    (while (< j (length ends))
      (setq b (nth j ends))
      (if (/= (jp:assocv 'ent a) (jp:assocv 'ent b))
        (progn
          (setq da (jp:assocv 'pt a)
                db (jp:assocv 'pt b)
                dxy (jp:dist2d da db)
                dz  (jp:abs (- (jp:z da) (jp:z db)))
                key (jp:pair-key "END" a b))
          (if (and (not (jp:skipped-p key))
                   (<= dxy tol)
                   (or (> dxy *JP-EPS*) (> dz *JP-EPS*)))
            (if (< dxy bd)
              (setq bd dxy best (list a b dxy dz))))))
      (setq j (1+ j)))
    (setq i (1+ i)))
  best)

(defun jp:fix-endpair-once (ents tol zmode mode / iss a b pa pb mv fx z targetFx targetMv title p key filp)
  (setq iss (jp:find-endpair ents tol))
  (if iss
    (progn
      (setq a (nth 0 iss) b (nth 1 iss)
            pa (jp:assocv 'pt a) pb (jp:assocv 'pt b)
            mv (jp:choose-mover a b)
            fx (if (= mv a) b a)
            z  (jp:desired-z (jp:z pa) (jp:z pb) zmode)
            filp (jp:terminal-fillet-point a b tol)
            p  (if filp filp (jp:mid pa pb))
            key (jp:pair-key "END" a b)
            title
              (cond
                (filp
                 (strcat "FILLET 0 / PONTAS | dXY=" (rtos (nth 2 iss) 2 4)))
                ((<= (nth 2 iss) *JP-EPS*)
                 (strcat "CONFLITO DE COTA | dz=" (rtos (nth 3 iss) 2 4)))
                (T
                 (strcat "GAP/PARALELO | dXY=" (rtos (nth 2 iss) 2 4)))))
      (if (jp:confirm-p mode title a b p tol)
        (progn
          (if filp
            ;; Havendo uma intersecao matematica unica, as DUAS pontas
            ;; vao para ela: comportamento equivalente ao FILLET R=0.
            (progn
              (jp:set-endpoint a (jp:ptz filp z))
              (jp:set-endpoint b (jp:ptz filp z)))
            ;; Paralelos / sem intersecao unica: aplica a hierarquia.
            ;; ARC > LINE; entre iguais, o segmento maior prevalece.
            (progn
              (setq targetFx (jp:ptz (jp:assocv 'pt fx) z))
              (jp:set-endpoint fx targetFx)
              (setq fx (jp:get-terminal-data (jp:assocv 'ent fx) (jp:assocv 'side fx)))
              (setq targetMv (jp:ptz (jp:assocv 'pt fx) z))
              (jp:set-endpoint mv targetMv)))
          (list T title))
        (progn
          (if (not *JP-STOP*) (jp:add-skip key))
          (list 'SKIP title))))
    nil))

;;; ---------------- endpoint x interior de curva -------------------

(defun jp:closest-point-xy (e p / basez q)
  ;; Usa o Z inicial da curva para que a procura seja guiada por XY.
  (setq basez (jp:z (vlax-curve-getStartPoint e)))
  (setq q (vlax-curve-getClosestPointTo e (jp:ptz p basez)))
  q)

(defun jp:near-endpoint-p (e p tol / s ept)
  (setq s (vlax-curve-getStartPoint e)
        ept (vlax-curve-getEndPoint e))
  (or (<= (jp:dist2d p s) tol) (<= (jp:dist2d p ept) tol)))

(defun jp:find-end-to-curve (ents tol / ends best bd d ce p q dxy dz key)
  (setq ends (jp:get-endpoints ents)
        best nil bd 1.0e99)
  (foreach d ends
    (setq p (jp:assocv 'pt d))
    (foreach ce ents
      (if (and (jp:curve-p ce)
               (/= ce (jp:assocv 'ent d)))
        (progn
          (setq q (jp:closest-point-xy ce p)
                dxy (jp:dist2d p q)
                dz (jp:abs (- (jp:z p) (jp:z q)))
                key (strcat "INT:" (jp:end-key d) "|" (jp:handle ce)))
          ;; Evita duplicar endpoint-endpoint; aqui queremos interior.
          (if (and (not (jp:skipped-p key))
                   (<= dxy tol)
                   (not (jp:near-endpoint-p ce q (* tol 0.20)))
                   (or (> dxy *JP-EPS*) (> dz *JP-EPS*)))
            (if (< dxy bd)
              (setq bd dxy best (list d ce q dxy dz)))))))
  best))

(defun jp:curve-local-data-at-point (e q / par sp ep p0 p1 pm a b d0 d1 kind)
  (setq par (vlax-curve-getParamAtPoint e q)
        sp (vlax-curve-getStartParam e)
        ep (vlax-curve-getEndParam e))
  (cond
    ((member (jp:etype e) '("LINE" "ARC")) (setq a sp b ep))
    (T
      (setq a (max sp (fix par))
            b (min ep (+ a 1.0)))
      (if (<= (- b a) 1.0e-9)
        (setq a (max sp (- b 1.0))))))
  (setq p0 (vlax-curve-getPointAtParam e a)
        p1 (vlax-curve-getPointAtParam e b)
        pm (vlax-curve-getPointAtParam e (/ (+ a b) 2.0))
        d0 (vlax-curve-getDistAtParam e a)
        d1 (vlax-curve-getDistAtParam e b)
        kind (jp:seg-kind p0 pm p1))
  (list (cons 'ent e) (cons 'side "INTERIOR") (cons 'pt q)
        (cons 'inner p0) (cons 'len (jp:abs (- d1 d0)))
        (cons 'kind kind) (cons 'typ (jp:etype e))))

(defun jp:fix-end-to-curve-once (ents tol zmode mode / iss d ce q p z target local key title eps seg keep src)
  (setq iss (jp:find-end-to-curve ents tol))
  (if iss
    (progn
      (setq d (nth 0 iss) ce (nth 1 iss) q (nth 2 iss)
            p (jp:assocv 'pt d)
            src (jp:assocv 'ent d)
            z (jp:desired-z (jp:z p) (jp:z q) zmode)
            target (list (car q) (cadr q) z)
            local (jp:curve-local-data-at-point ce q)
            key (strcat "INT:" (jp:end-key d) "|" (jp:handle ce))
            eps (jp:node-eps tol)
            title (strcat "PONTA -> INTERIOR / FILLET R=0 TOPOLOGICO | dXY="
                          (rtos (nth 3 iss) 2 4)))
      (if (null local)
        (progn (jp:add-skip key) nil)
        (if (jp:confirm-p mode title d local (jp:mid p q) tol)
          (progn
            ;; Se o receptor passa pelo no, ele precisa deixar de ser um T:
            ;; divide no ponto e descarta o lado com MENOS vertices.
            (setq seg (jp:segment-at-point ce q)
                  keep (if seg (jp:trim-curve-to-node ce seg q eps) ce))
            (if (and keep (jp:alive-p keep) (jp:alive-p src))
              (progn
                (setq ents (jp:add-new-pieces ents (list keep src))
                      ents (vl-remove-if-not 'jp:alive-p ents))
                ;; Primeiro leva a ponta invasora ao no; depois grava a mesma
                ;; cota nas duas entidades sobreviventes.
                (jp:set-endpoint d target)
                (jp:set-node-z-pieces (list keep src) q z (* eps 10.0))
                (list T title ents))
              (progn
                (jp:add-skip key)
                (list nil title (vl-remove-if-not 'jp:alive-p ents)))))
          (progn
            (if (not *JP-STOP*) (jp:add-skip key))
            (list 'SKIP title ents)))))
    nil))

;;; ---------------- overlap: LINE x LINE ---------------------------

(defun jp:point-line-offset2d (p a b / vx vy wx wy L)
  (setq vx (- (car b) (car a))
        vy (- (cadr b) (cadr a))
        wx (- (car p) (car a))
        wy (- (cadr p) (cadr a))
        L (sqrt (+ (* vx vx) (* vy vy))))
  (if (> L 1.0e-12)
    (/ (jp:abs (- (* vx wy) (* vy wx))) L)
    1.0e99))

(defun jp:proj-scalar (p a ux uy)
  (+ (* (- (car p) (car a)) ux)
     (* (- (cadr p) (cadr a)) uy)))

(defun jp:line-overlap-info (ea eb tol / a1 a2 b1 b2 vx vy L ux uy off1 off2 s1 s2 amin amax bmin bmax ov)
  (setq a1 (vlax-curve-getStartPoint ea)
        a2 (vlax-curve-getEndPoint ea)
        b1 (vlax-curve-getStartPoint eb)
        b2 (vlax-curve-getEndPoint eb)
        vx (- (car a2) (car a1))
        vy (- (cadr a2) (cadr a1))
        L  (sqrt (+ (* vx vx) (* vy vy))))
  (if (> L 1.0e-12)
    (progn
      (setq ux (/ vx L) uy (/ vy L)
            off1 (jp:point-line-offset2d b1 a1 a2)
            off2 (jp:point-line-offset2d b2 a1 a2))
      (if (and (<= off1 tol) (<= off2 tol))
        (progn
          (setq s1 (jp:proj-scalar b1 a1 ux uy)
                s2 (jp:proj-scalar b2 a1 ux uy)
                amin 0.0 amax L
                bmin (min s1 s2) bmax (max s1 s2)
                ov (- (min amax bmax) (max amin bmin)))
          (if (> ov *JP-EPS*)
            (list a1 a2 b1 b2 ux uy amin amax bmin bmax ov)))))))

(defun jp:find-line-overlap (ents tol / i j ea eb info best ov)
  (setq i 0 best nil ov 0.0)
  (while (< i (length ents))
    (setq ea (nth i ents) j (1+ i))
    (if (and (jp:alive-p ea) (= (jp:etype ea) "LINE"))
      (while (< j (length ents))
        (setq eb (nth j ents))
        (if (and (jp:alive-p eb) (= (jp:etype eb) "LINE")
                 (not (jp:skipped-p (jp:entpair-key "OV" ea eb)))
                 (setq info (jp:line-overlap-info ea eb tol)))
          (if (> (nth 10 info) ov)
            (setq ov (nth 10 info) best (list ea eb info))))
        (setq j (1+ j))))
    (setq i (1+ i)))
  best)

(defun jp:line-data (e / p0 p1)
  (setq p0 (vlax-curve-getStartPoint e)
        p1 (vlax-curve-getEndPoint e))
  (list (cons 'ent e) (cons 'side "START") (cons 'pt p0)
        (cons 'inner p1) (cons 'len (distance p0 p1))
        (cons 'kind "LINE") (cons 'typ "LINE")))

(defun jp:erase-ent (e)
  (if (jp:alive-p e) (vla-delete (vlax-ename->vla-object e))))

(defun jp:trim-overlap-lines (eref emov info zmode / r1 r2 m1 m2 ux uy rmin rmax sm1 sm2 mmin mmax z pnew obj dstart dend)
  ;; Ajusta APENAS a linha móvel para retirar o trecho duplicado.
  (setq r1 (vlax-curve-getStartPoint eref)
        r2 (vlax-curve-getEndPoint eref)
        m1 (vlax-curve-getStartPoint emov)
        m2 (vlax-curve-getEndPoint emov)
        ux (nth 4 info) uy (nth 5 info)
        rmin 0.0 rmax (jp:dist2d r1 r2)
        sm1 (jp:proj-scalar m1 r1 ux uy)
        sm2 (jp:proj-scalar m2 r1 ux uy)
        mmin (min sm1 sm2) mmax (max sm1 sm2))
  (cond
    ;; Móvel totalmente contida na referência -> redundante.
    ((and (>= mmin (- rmin *JP-EPS*)) (<= mmax (+ rmax *JP-EPS*)))
     (jp:erase-ent emov)
     'ERASED)

    ;; Sobra à esquerda: endpoint sobreposto vai para o início da referência.
    ((and (< mmin rmin) (> mmax rmin) (<= mmax (+ rmax *JP-EPS*)))
     (setq z (jp:desired-z
               (jp:z (if (< sm1 sm2) m2 m1))
               (jp:z r1) zmode)
           pnew (jp:ptz r1 z))
     (setq obj (vlax-ename->vla-object emov))
     (if (< sm1 sm2)
       (vla-put-EndPoint obj (vlax-3d-point pnew))
       (vla-put-StartPoint obj (vlax-3d-point pnew)))
     (vla-Update obj)
     'TRIMMED)

    ;; Sobra à direita: endpoint sobreposto vai para o fim da referência.
    ((and (>= mmin (- rmin *JP-EPS*)) (< mmin rmax) (> mmax rmax))
     (setq z (jp:desired-z
               (jp:z (if (> sm1 sm2) m1 m2))
               (jp:z r2) zmode)
           pnew (jp:ptz r2 z))
     (setq obj (vlax-ename->vla-object emov))
     (if (> sm1 sm2)
       (vla-put-StartPoint obj (vlax-3d-point pnew))
       (vla-put-EndPoint obj (vlax-3d-point pnew)))
     (vla-Update obj)
     'TRIMMED)
    (T nil)))

(defun jp:fix-line-overlap-once (ents tol zmode mode / iss ea eb info da db mv ref title p res key)
  (setq iss (jp:find-line-overlap ents tol))
  (if iss
    (progn
      (setq ea (nth 0 iss) eb (nth 1 iss) info (nth 2 iss)
            da (jp:line-data ea) db (jp:line-data eb)
            mv (jp:choose-mover da db)
            ref (if (= (jp:assocv 'ent mv) ea) eb ea)
            title (strcat "OVERLAP LINEAR | comprimento=" (rtos (nth 10 info) 2 4))
            key (jp:entpair-key "OV" ea eb)
            p (jp:mid (nth 0 info) (nth 1 info)))
      (if (jp:confirm-p mode title da db p tol)
        (progn
          ;; Se B foi escolhida como referência mas info foi calculada em A,
          ;; recalcula no sentido correto.
          (if (= ref eb)
            (setq info (jp:line-overlap-info eb ea tol)))
          (setq res (jp:trim-overlap-lines ref (jp:assocv 'ent mv) info zmode))
          (if res (list T title) (list nil title)))
        (progn
          (if (not *JP-STOP*) (jp:add-skip key))
          (list 'SKIP title))))
    nil))

;;; ---------------- interseções reais LINE x LINE ------------------

(defun jp:line-intersection2d (e1 e2 / p1 p2 p3 p4 x1 y1 x2 y2 x3 y3 x4 y4 den ta u px py z1 z2)
  (setq p1 (vlax-curve-getStartPoint e1)
        p2 (vlax-curve-getEndPoint e1)
        p3 (vlax-curve-getStartPoint e2)
        p4 (vlax-curve-getEndPoint e2)
        x1 (car p1) y1 (cadr p1) x2 (car p2) y2 (cadr p2)
        x3 (car p3) y3 (cadr p3) x4 (car p4) y4 (cadr p4)
        den (- (* (- x1 x2) (- y3 y4)) (* (- y1 y2) (- x3 x4))))
  (if (> (jp:abs den) 1.0e-12)
    (progn
      (setq ta (/ (- (* (- x1 x3) (- y3 y4)) (* (- y1 y3) (- x3 x4))) den)
            u (/ (- (* (- x1 x3) (- y1 y2)) (* (- y1 y3) (- x1 x2))) den))
      (if (and (> ta 1.0e-8) (< ta (- 1.0 1.0e-8))
               (> u 1.0e-8) (< u (- 1.0 1.0e-8)))
        (progn
          (setq px (+ x1 (* ta (- x2 x1)))
                py (+ y1 (* ta (- y2 y1)))
                z1 (+ (jp:z p1) (* ta (- (jp:z p2) (jp:z p1))))
                z2 (+ (jp:z p3) (* u (- (jp:z p4) (jp:z p3)))))
          (list px py z1 z2 ta u))))))

(defun jp:split-line-at (e p z / obj o1 o2 e1 e2 p1 p2)
  ;; Divide uma LINE em duas por cópia do objeto original, preservando
  ;; layer, cor, tipo de linha, lineweight e demais propriedades.
  (setq obj (vlax-ename->vla-object e)
        p1 (vlax-curve-getStartPoint e)
        p2 (vlax-curve-getEndPoint e)
        p  (jp:ptz p z)
        o1 (vla-Copy obj)
        o2 (vla-Copy obj)
        e1 (vlax-vla-object->ename o1)
        e2 (vlax-vla-object->ename o2))
  (vla-put-StartPoint o1 (vlax-3d-point p1))
  (vla-put-EndPoint   o1 (vlax-3d-point p))
  (vla-put-StartPoint o2 (vlax-3d-point p))
  (vla-put-EndPoint   o2 (vlax-3d-point p2))
  (vla-Update o1)
  (vla-Update o2)
  (jp:erase-ent e)
  (list e1 e2))

(defun jp:find-line-cross (ents / i j e1 e2 ip)
  (setq i 0)
  (while (< i (length ents))
    (setq e1 (nth i ents) j (1+ i))
    (if (and (jp:alive-p e1) (= (jp:etype e1) "LINE"))
      (while (< j (length ents))
        (setq e2 (nth j ents))
        (if (and (jp:alive-p e2) (= (jp:etype e2) "LINE")
                 (not (jp:skipped-p (jp:entpair-key "CROSS" e1 e2)))
                 (setq ip (jp:line-intersection2d e1 e2)))
          (progn (setq j (length ents) i (length ents))))
        (if (< j (length ents)) (setq j (1+ j)))))
    (if (< i (length ents)) (setq i (1+ i))))
  (if ip (list e1 e2 ip) nil))

(defun jp:replace-ent-with-list (ents old newlist / out e)
  (setq out '())
  (foreach e ents
    (if (= e old)
      (foreach n newlist (setq out (cons n out)))
      (setq out (cons e out))))
  (reverse out))

(defun jp:fix-line-cross-once (ents tol zmode mode / iss e1 e2 ip z title p da db n1 n2 key)
  (setq iss (jp:find-line-cross ents))
  (if iss
    (progn
      (setq e1 (nth 0 iss) e2 (nth 1 iss) ip (nth 2 iss)
            z (jp:desired-z (nth 2 ip) (nth 3 ip) zmode)
            p (list (nth 0 ip) (nth 1 ip) z)
            da (jp:line-data e1) db (jp:line-data e2)
            key (jp:entpair-key "CROSS" e1 e2)
            title "INTERSECAO INTERIOR LINE x LINE | criando no de conexao")
      (if (jp:confirm-p mode title da db p tol)
        (progn
          (setq n1 (jp:split-line-at e1 p z)
                ents (jp:replace-ent-with-list ents e1 n1)
                n2 (jp:split-line-at e2 p z)
                ents (jp:replace-ent-with-list ents e2 n2))
          (list T title ents))
        (progn
          (if (not *JP-STOP*) (jp:add-skip key))
          (list 'SKIP title ents))))
    nil))


(defun jp:poly3d-p (e / ed flag)
  (if (= (jp:etype e) "POLYLINE")
    (progn
      (setq ed (entget e)
            flag (if (assoc 70 ed) (cdr (assoc 70 ed)) 0))
      (/= 0 (logand flag 8)))
    nil))

(defun jp:intersections3d (e1 e2 / o1 o2 v a lst out)
  (setq o1 (vlax-ename->vla-object e1)
        o2 (vlax-ename->vla-object e2)
        v  (vl-catch-all-apply 'vla-IntersectWith (list o1 o2 0)))
  (if (or (vl-catch-all-error-p v) (null v))
    nil
    (progn
      (setq a (vl-catch-all-apply 'vlax-variant-value (list v)))
      (if (vl-catch-all-error-p a)
        nil
        (progn
          (setq lst (vl-catch-all-apply 'vlax-safearray->list (list a)))
          (if (vl-catch-all-error-p lst)
            nil
            (progn
              (setq out '())
              (while (>= (length lst) 3)
                (setq out (cons (list (car lst) (cadr lst) (caddr lst)) out)
                      lst (cdddr lst)))
              (reverse out))))))))

(defun jp:interior-point-p (e p tol / s en d)
  (if (jp:closed-p e)
    nil
    (progn
      (setq s (vlax-curve-getStartPoint e)
            en (vlax-curve-getEndPoint e)
            d (max (* tol 0.05) 1.0e-7))
      (and (> (jp:dist2d p s) d)
           (> (jp:dist2d p en) d)))))

(defun jp:find-generic-cross (ents tol / i j e1 e2 pts p key found)
  (setq i 0 found nil)
  (while (and (< i (length ents)) (null found))
    (setq e1 (nth i ents) j (1+ i))
    (if (and (jp:curve-p e1) (not (jp:closed-p e1)))
      (while (and (< j (length ents)) (null found))
        (setq e2 (nth j ents))
        (if (and (jp:curve-p e2)
                 (not (jp:closed-p e2))
                 ;; LINE x LINE já possui tratamento XY próprio, inclusive com Z diferente.
                 (not (and (= (jp:etype e1) "LINE") (= (jp:etype e2) "LINE")))
                 (not (jp:skipped-p (jp:entpair-key "GCROSS" e1 e2))))
          (progn
            (setq pts (jp:intersections3d e1 e2))
            (foreach p pts
              (if (and (null found)
                       (jp:interior-point-p e1 p tol)
                       (jp:interior-point-p e2 p tol))
                (setq found (list e1 e2 p))))))
        (setq j (1+ j))))
    (setq i (1+ i)))
  found)

(defun jp:break-at-point (e p / before after ret)
  ;; 2D: BREAKATPOINT. 3D POLYLINE: fallback com BREAK no mesmo ponto.
  ;; A chamada é protegida; se a versão/objeto não aceitar, retorna nil.
  (setq before (entlast))
  (if (jp:poly3d-p e)
    (setq ret
      (vl-catch-all-apply
        '(lambda () (command-s "_.BREAK" e "_F" (trans p 0 1) "@"))))
    (setq ret
      (vl-catch-all-apply
        '(lambda () (command-s "_.BREAKATPOINT" e (trans p 0 1))))))
  (if (vl-catch-all-error-p ret)
    nil
    (progn
      (setq after (entlast))
      (if (and after (/= after before) (jp:curve-p after))
        (list e after)
        (if (jp:alive-p e) (list e) nil)))))

(defun jp:fix-generic-cross-once (ents tol mode / iss e1 e2 p d1 d2 key title n1 n2)
  (setq iss (jp:find-generic-cross ents tol))
  (if iss
    (progn
      (setq e1 (nth 0 iss) e2 (nth 1 iss) p (nth 2 iss)
            d1 (jp:get-terminal-data e1 "START")
            d2 (jp:get-terminal-data e2 "START")
            key (jp:entpair-key "GCROSS" e1 e2)
            title "INTERSECAO INTERIOR ARC/POLYLINE | criando no de conexao")
      (if (jp:confirm-p mode title d1 d2 p tol)
        (progn
          (setq n1 (jp:break-at-point e1 p)
                n2 (jp:break-at-point e2 p))
          (if (and n1 n2)
            (progn
              ;; Adiciona novos objetos gerados pelo BREAKATPOINT à lista.
              (foreach n n1 (if (not (member n ents)) (setq ents (cons n ents))))
              (foreach n n2 (if (not (member n ents)) (setq ents (cons n ents))))
              (list T title ents))
            (list nil title ents)))
        (progn
          (if (not *JP-STOP*) (jp:add-skip key))
          (list 'SKIP title ents))))
    nil))

;;; -------------------------- JOIN final ----------------------------

(defun jp:make-ss (ents / ss e)
  (setq ss (ssadd))
  (foreach e ents
    (if (jp:alive-p e) (ssadd e ss)))
  ss)

(defun jp:run-overkill (ents tol mode / ss ans ret)
  ;; Fallback final para overlaps/duplicatas mais complexos envolvendo
  ;; LINE / ARC / POLYLINE. As correcoes hierarquicas do JPLUS rodam antes.
  (setq ss (jp:make-ss ents))
  (if (> (sslength ss) 1)
    (progn
      (setq ans "Sim")
      (if (= mode "ASK")
        (progn
          (initget "Sim Nao")
          (setq ans (getkword
            "\n[JPLUS] Aplicar limpeza final de overlaps/duplicatas restantes? [Sim/Nao] <Sim>: "))
          (if (null ans) (setq ans "Sim"))))
      (if (= ans "Sim")
        (progn
          ;; -OVERKILL: O=tolerance, P=optimize polylines,
          ;; T=combine partial overlap, E=combine end-to-end.
          (setq ret
            (vl-catch-all-apply
              '(lambda ()
                 (command-s "_.-OVERKILL" ss ""
                          "_O" tol
                          "_P" "_Y"
                          "_T" "_Y"
                          "_E" "_Y"
                          ""))))
          (not (vl-catch-all-error-p ret)))
        nil))
    nil))

(defun jp:pedit-joinable-p (e / typ)
  ;; PEDIT Multiple e o consolidado mais confiavel para geometria 2D.
  ;; POLYLINE 3D fica de fora e recebe fallback pelo JOIN nativo.
  (and (jp:alive-p e)
       (not (jp:closed-p e))
       (setq typ (jp:etype e))
       (or (= typ "LINE")
           (= typ "ARC")
           (= typ "LWPOLYLINE")
           (and (= typ "POLYLINE") (not (jp:poly3d-p e))))))

(defun jp:make-pedit-ss (ents / ss e)
  (setq ss (ssadd))
  (foreach e ents
    (if (jp:pedit-joinable-p e) (ssadd e ss)))
  ss)

(defun jp:make-3d-ss (ents / ss e)
  (setq ss (ssadd))
  (foreach e ents
    (if (and (jp:alive-p e)
             (not (jp:closed-p e))
             (= (jp:etype e) "POLYLINE")
             (jp:poly3d-p e))
      (ssadd e ss)))
  ss)

(defun jp:run-pedit-join (ents tol / ss ss3 oldpea ret ret3 ok)
  ;; PEDIT Multiple + Join + Jointype Extend e o "FILLET R=0" multiplo
  ;; do AutoCAD: estende/apara para os endpoints mais proximos.
  ;; O fuzz usa a tolerancia digitada pelo usuario.
  (setq ss (jp:make-pedit-ss ents)
        ss3 (jp:make-3d-ss ents)
        oldpea (getvar "PEDITACCEPT")
        ok nil)

  (if (> (sslength ss) 1)
    (progn
      (setvar "PEDITACCEPT" 1)
      (setq ret
        (vl-catch-all-apply
          '(lambda ()
             (command-s "_.PEDIT" "_M" ss ""
                      "_J" "_J" "_E" tol ""))))
      (if (not (vl-catch-all-error-p ret)) (setq ok T))))

  ;; 3D POLYLINE nao participa do PEDIT 2D; tenta JOIN nativo depois
  ;; que as pontas ja foram normalizadas pelo JPLUS.
  (if (> (sslength ss3) 1)
    (progn
      (setq ret3
        (vl-catch-all-apply
          '(lambda () (command-s "_.JOIN" ss3 ""))))
      (if (not (vl-catch-all-error-p ret3)) (setq ok T))))

  ;; Fallback final importante: JOIN nativo tambem consegue formar 3D
  ;; polylines quando objetos end-to-end nao estao no mesmo plano.
  ;; Reconstrui o selection set APOS o PEDIT porque varias entidades
  ;; podem ter sido absorvidas/deletadas pela consolidacao anterior.
  (setq ss (jp:make-ss (vl-remove-if-not 'jp:alive-p ents)))
  (if (> (sslength ss) 1)
    (progn
      (setq ret3
        (vl-catch-all-apply
          '(lambda () (command-s "_.JOIN" ss ""))))
      (if (not (vl-catch-all-error-p ret3)) (setq ok T))))

  (setvar "PEDITACCEPT" oldpea)
  ok)

;;; =================================================================
;;; v1.4 - motor FILLET 0 multiplo / intersecoes XY
;;; =================================================================

(defun jp:node-eps (tol)
  (max 1.0e-7 (min 1.0e-5 (* tol 0.001))))

(defun jp:clamp01 (x)
  (cond ((< x 0.0) 0.0) ((> x 1.0) 1.0) (T x)))

(defun jp:pt-eq2d-p (a b eps)
  (<= (jp:dist2d a b) eps))

(defun jp:dedupe-pts2d (pts eps / out p found q)
  (setq out '())
  (foreach p pts
    (setq found nil)
    (foreach q out
      (if (jp:pt-eq2d-p p q eps) (setq found T)))
    (if (not found) (setq out (cons p out))))
  (reverse out))

(defun jp:make-seg-data (e a b / p0 p1 pm kind len cc r a0 am a1 ccw)
  (setq p0 (vlax-curve-getPointAtParam e a)
        p1 (vlax-curve-getPointAtParam e b)
        pm (vlax-curve-getPointAtParam e (/ (+ a b) 2.0))
        kind (jp:seg-kind p0 pm p1)
        len (jp:abs (- (vlax-curve-getDistAtParam e b)
                       (vlax-curve-getDistAtParam e a))))
  (if (= kind "ARC")
    (progn
      (setq cc (jp:circumcenter2d p0 pm p1))
      (if cc
        (progn
          (setq r   (jp:dist2d cc p0)
                a0  (angle cc p0)
                am  (angle cc pm)
                a1  (angle cc p1)
                ccw (jp:between-ccw-p a0 am a1))))))
  (list (cons 'ent e)
        (cons 'a a) (cons 'b b)
        (cons 'p0 p0) (cons 'p1 p1) (cons 'pm pm)
        (cons 'kind kind) (cons 'len len)
        (cons 'cc cc) (cons 'r r)
        (cons 'a0 a0) (cons 'a1 a1) (cons 'ccw ccw)))

(defun jp:curve-segments (e / typ sp ep out i a b)
  (setq out '())
  (if (jp:curve-p e)
    (progn
      (setq typ (jp:etype e)
            sp (vlax-curve-getStartParam e)
            ep (vlax-curve-getEndParam e))
      (cond
        ((member typ '("LINE" "ARC"))
         (setq out (list (jp:make-seg-data e sp ep))))
        (T
         ;; Em POLYLINE/LWPOLYLINE cada intervalo inteiro de parametro
         ;; corresponde a um segmento, inclusive bulges/arcos.
         (setq i (fix sp))
         (while (< i (- ep *JP-EPS*))
           (setq a (max sp (* 1.0 i))
                 b (min ep (+ (* 1.0 i) 1.0)))
           (if (> (- b a) *JP-EPS*)
             (setq out (cons (jp:make-seg-data e a b) out)))
           (setq i (1+ i)))
         (setq out (reverse out)))))
  out))

(defun jp:all-segments (ents / out e s)
  (setq out '())
  (foreach e ents
    (if (jp:alive-p e)
      (foreach s (jp:curve-segments e)
        (setq out (cons s out)))))
  (reverse out))

(defun jp:seg-display-data (s)
  (list (cons 'ent (jp:assocv 'ent s))
        (cons 'side "INTERIOR")
        (cons 'pt (jp:assocv 'pm s))
        (cons 'inner (jp:assocv 'p0 s))
        (cons 'len (jp:assocv 'len s))
        (cons 'kind (jp:assocv 'kind s))
        (cons 'typ (jp:etype (jp:assocv 'ent s)))))

(defun jp:line-line-ints2d (s1 s2 / p1 p2 p3 p4 x1 y1 x2 y2 x3 y3 x4 y4 den t1 t2 px py)
  (setq p1 (jp:assocv 'p0 s1) p2 (jp:assocv 'p1 s1)
        p3 (jp:assocv 'p0 s2) p4 (jp:assocv 'p1 s2)
        x1 (car p1) y1 (cadr p1) x2 (car p2) y2 (cadr p2)
        x3 (car p3) y3 (cadr p3) x4 (car p4) y4 (cadr p4)
        den (- (* (- x1 x2) (- y3 y4))
               (* (- y1 y2) (- x3 x4))))
  (if (> (jp:abs den) 1.0e-12)
    (progn
      (setq t1 (/ (- (* (- x1 x3) (- y3 y4))
                       (* (- y1 y3) (- x3 x4))) den)
            t2 (/ (- (* (- x1 x3) (- y1 y2))
                       (* (- y1 y3) (- x1 x2))) den))
      (if (and (>= t1 -1.0e-9) (<= t1 (+ 1.0 1.0e-9))
               (>= t2 -1.0e-9) (<= t2 (+ 1.0 1.0e-9)))
        (progn
          (setq px (+ x1 (* t1 (- x2 x1)))
                py (+ y1 (* t1 (- y2 y1))))
          (list (list px py 0.0)))))))

(defun jp:arc-point-p (s p / cc ap a0 a1 ccw)
  (setq cc (jp:assocv 'cc s)
        ap (angle cc p)
        a0 (jp:assocv 'a0 s)
        a1 (jp:assocv 'a1 s)
        ccw (jp:assocv 'ccw s))
  (if ccw
    (jp:between-ccw-p a0 ap a1)
    (jp:between-ccw-p a1 ap a0)))

(defun jp:line-arc-ints2d (sl sa / p0 p1 cc r dx dy fx fy aa bb c disc sd t1 t2 out p)
  (setq p0 (jp:assocv 'p0 sl)
        p1 (jp:assocv 'p1 sl)
        cc (jp:assocv 'cc sa)
        r  (jp:assocv 'r sa)
        out '())
  (if (and cc r (> r 1.0e-12))
    (progn
      (setq dx (- (car p1) (car p0))
            dy (- (cadr p1) (cadr p0))
            fx (- (car p0) (car cc))
            fy (- (cadr p0) (cadr cc))
            aa (+ (* dx dx) (* dy dy))
            bb (* 2.0 (+ (* fx dx) (* fy dy)))
            c  (- (+ (* fx fx) (* fy fy)) (* r r)))
      (if (> aa 1.0e-16)
        (progn
          (setq disc (- (* bb bb) (* 4.0 aa c)))
          (if (>= disc -1.0e-9)
            (progn
              (if (< disc 0.0) (setq disc 0.0))
              (setq sd (sqrt disc)
                    t1 (/ (+ (- bb) sd) (* 2.0 aa))
                    t2 (/ (- (- bb) sd) (* 2.0 aa)))
              (foreach tt (list t1 t2)
                (if (and (>= tt -1.0e-9) (<= tt (+ 1.0 1.0e-9)))
                  (progn
                    (setq p (list (+ (car p0) (* tt dx))
                                  (+ (cadr p0) (* tt dy)) 0.0))
                    (if (jp:arc-point-p sa p)
                      (setq out (cons p out))))))))))))
  (jp:dedupe-pts2d (reverse out) 1.0e-8))

(defun jp:arc-arc-ints2d (s1 s2 / c1 c2 r1 r2 d a h x2 y2 rx ry p1 p2 out)
  (setq c1 (jp:assocv 'cc s1) c2 (jp:assocv 'cc s2)
        r1 (jp:assocv 'r s1) r2 (jp:assocv 'r s2)
        out '())
  (if (and c1 c2 r1 r2)
    (progn
      (setq d (jp:dist2d c1 c2))
      (if (and (> d 1.0e-12)
               (<= d (+ r1 r2 1.0e-8))
               (>= d (- (jp:abs (- r1 r2)) 1.0e-8)))
        (progn
          (setq a (/ (+ (* r1 r1) (- (* r2 r2)) (* d d)) (* 2.0 d))
                h (sqrt (max 0.0 (- (* r1 r1) (* a a))))
                x2 (+ (car c1) (* a (/ (- (car c2) (car c1)) d)))
                y2 (+ (cadr c1) (* a (/ (- (cadr c2) (cadr c1)) d)))
                rx (* (- (/ (- (cadr c2) (cadr c1)) d)) h)
                ry (* (/ (- (car c2) (car c1)) d) h)
                p1 (list (+ x2 rx) (+ y2 ry) 0.0)
                p2 (list (- x2 rx) (- y2 ry) 0.0))
          (foreach p (list p1 p2)
            (if (and (jp:arc-point-p s1 p) (jp:arc-point-p s2 p))
              (setq out (cons p out))))))))
  (jp:dedupe-pts2d (reverse out) 1.0e-8))

(defun jp:seg-ints2d (s1 s2 / k1 k2)
  (setq k1 (jp:assocv 'kind s1)
        k2 (jp:assocv 'kind s2))
  (cond
    ((and (= k1 "LINE") (= k2 "LINE"))
     (jp:line-line-ints2d s1 s2))
    ((and (= k1 "LINE") (= k2 "ARC"))
     (jp:line-arc-ints2d s1 s2))
    ((and (= k1 "ARC") (= k2 "LINE"))
     (jp:line-arc-ints2d s2 s1))
    ((and (= k1 "ARC") (= k2 "ARC"))
     (jp:arc-arc-ints2d s1 s2))
    (T nil)))

(defun jp:arc-frac-at-pt (s p / cc a0 a1 ap total part)
  (setq cc (jp:assocv 'cc s)
        a0 (jp:assocv 'a0 s)
        a1 (jp:assocv 'a1 s)
        ap (angle cc p))
  (if (jp:assocv 'ccw s)
    (setq total (jp:ccw-delta a0 a1)
          part  (jp:ccw-delta a0 ap))
    (setq total (jp:ccw-delta a1 a0)
          part  (jp:ccw-delta ap a0)))
  (if (> total 1.0e-12) (jp:clamp01 (/ part total)) 0.0))

(defun jp:line-frac-at-pt (s p / p0 p1 dx dy den frac)
  (setq p0 (jp:assocv 'p0 s)
        p1 (jp:assocv 'p1 s)
        dx (- (car p1) (car p0))
        dy (- (cadr p1) (cadr p0))
        den (+ (* dx dx) (* dy dy)))
  (if (> den 1.0e-16)
    (setq frac (/ (+ (* (- (car p) (car p0)) dx)
                       (* (- (cadr p) (cadr p0)) dy)) den))
    (setq frac 0.0))
  (jp:clamp01 frac))

(defun jp:seg-param-at-xy (s p / f a b)
  (setq f (if (= (jp:assocv 'kind s) "ARC")
            (jp:arc-frac-at-pt s p)
            (jp:line-frac-at-pt s p))
        a (jp:assocv 'a s)
        b (jp:assocv 'b s))
  (+ a (* f (- b a))))

(defun jp:seg-point3d-at-xy (s p / e par q)
  (setq e (jp:assocv 'ent s)
        par (jp:seg-param-at-xy s p)
        q (vl-catch-all-apply 'vlax-curve-getPointAtParam (list e par)))
  (if (vl-catch-all-error-p q)
    (list (car p) (cadr p)
          (+ (jp:z (jp:assocv 'p0 s))
             (* (jp:line-frac-at-pt s p)
                (- (jp:z (jp:assocv 'p1 s))
                   (jp:z (jp:assocv 'p0 s))))))
    q))

(defun jp:entity-interior-xy-p (e p eps / s en)
  (if (jp:closed-p e)
    T
    (progn
      (setq s (vlax-curve-getStartPoint e)
            en (vlax-curve-getEndPoint e))
      (and (> (jp:dist2d p s) eps)
           (> (jp:dist2d p en) eps)))))

(defun jp:xycross-key (e1 e2 p / base)
  (setq base (jp:entpair-key "XYCROSS" e1 e2))
  (strcat base ":"
          (rtos (car p) 2 6) "," (rtos (cadr p) 2 6)))

(defun jp:curve-length (e / ep val)
  (if (and (jp:alive-p e) (jp:curve-p e))
    (progn
      (setq ep (vlax-curve-getEndParam e)
            val (vl-catch-all-apply 'vlax-curve-getDistAtParam (list e ep)))
      (if (vl-catch-all-error-p val) 0.0 val))
    0.0))

(defun jp:tip-cost-at-xy (seg p / e par d total val)
  ;; Comprimento da menor ponta entre a intersecao e um endpoint.
  ;; Usado para processar primeiro os cruzamentos que exigem o menor corte.
  (setq e (jp:assocv 'ent seg)
        par (jp:seg-param-at-xy seg p)
        val (vl-catch-all-apply 'vlax-curve-getDistAtParam (list e par))
        total (jp:curve-length e))
  (if (vl-catch-all-error-p val)
    1.0e99
    (progn
      (setq d val)
      (min d (max 0.0 (- total d))))))

(defun jp:find-xy-cross (ents tol / segs i j s1 s2 e1 e2 pts p q1 q2 z1 z2 dz eps key best score bestscore)
  ;; Procura TODAS as intersecoes XY e escolhe primeiro a que exige
  ;; remover menos comprimento de ponta. Isso deixa a selecao multipla
  ;; muito mais previsivel do que simplesmente pegar o primeiro par.
  (setq segs (jp:all-segments ents)
        i 0 best nil bestscore 1.0e99
        eps (jp:node-eps tol))
  (while (< i (length segs))
    (setq s1 (nth i segs)
          e1 (jp:assocv 'ent s1)
          j (1+ i))
    (while (< j (length segs))
      (setq s2 (nth j segs)
            e2 (jp:assocv 'ent s2))
      (if (and (/= e1 e2) (jp:alive-p e1) (jp:alive-p e2))
        (progn
          (setq pts (jp:seg-ints2d s1 s2))
          (foreach p pts
            (setq q1 (jp:seg-point3d-at-xy s1 p)
                  q2 (jp:seg-point3d-at-xy s2 p)
                  z1 (jp:z q1) z2 (jp:z q2)
                  dz (jp:abs (- z1 z2))
                  key (jp:xycross-key e1 e2 p))
            ;; Resolvido apenas quando ja e endpoint das duas entidades e Z bate.
            (if (and (not (jp:skipped-p key))
                     (or (jp:entity-interior-xy-p e1 p eps)
                         (jp:entity-interior-xy-p e2 p eps)
                         (> dz eps)))
              (progn
                (setq score (+ (jp:tip-cost-at-xy s1 p)
                               (jp:tip-cost-at-xy s2 p)))
                (if (< score bestscore)
                  (setq bestscore score
                        best (list s1 s2 p q1 q2 dz key))))))))
      (setq j (1+ j)))
    (setq i (1+ i)))
  best)

(defun jp:add-new-pieces (ents pieces / n)
  (foreach n pieces
    (if (and (jp:alive-p n) (not (member n ents)))
      (setq ents (cons n ents))))
  ents)

(defun jp:set-node-z-pieces (pieces p z eps / e s en d)
  (foreach e pieces
    (if (and (jp:alive-p e) (jp:curve-p e) (not (jp:closed-p e)))
      (progn
        (setq s (vlax-curve-getStartPoint e)
              en (vlax-curve-getEndPoint e))
        (if (<= (jp:dist2d s p) eps)
          (progn
            (setq d (jp:get-terminal-data e "START"))
            (if d (jp:set-endpoint d (jp:ptz s z)))))
        (if (and (jp:alive-p e)
                 (<= (jp:dist2d (vlax-curve-getEndPoint e) p) eps))
          (progn
            (setq d (jp:get-terminal-data e "END"))
            (if d (jp:set-endpoint d
                    (jp:ptz (vlax-curve-getEndPoint e) z))))))))
  T)

(defun jp:keep-longer-after-break (pieces / a b la lb keep kill)
  ;; BREAKATPOINT gera duas partes. A "ponta" menor e apagada e a
  ;; parte maior e preservada. Em empate, preserva a primeira entidade.
  (cond
    ((null pieces) nil)
    ((= (length pieces) 1) (car pieces))
    (T
      (setq a (car pieces)
            b (cadr pieces)
            la (jp:curve-length a)
            lb (jp:curve-length b))
      (if (>= la lb)
        (setq keep a kill b)
        (setq keep b kill a))
      (if (and kill (jp:alive-p kill)) (jp:erase-ent kill))
      keep)))

(defun jp:curve-vertex-count (e / typ d n x)
  ;; Numero de vertices REAIS da entidade. Para LINE/ARC sao 2.
  ;; Em LWPOLYLINE usa o grupo 90; em POLYLINE antiga percorre VERTEX.
  ;; Se algo falhar, usa parametros da curva como fallback.
  (if (not (jp:alive-p e))
    0
    (progn
      (setq typ (jp:etype e))
      (cond
        ((member typ '("LINE" "ARC")) 2)
        ((= typ "LWPOLYLINE")
         (setq d (entget e)
               n (cdr (assoc 90 d)))
         (if (and (numberp n) (> n 0)) n 0))
        ((= typ "POLYLINE")
         (setq n 0 x (entnext e))
         (while (and x (/= (cdr (assoc 0 (entget x))) "SEQEND"))
           (if (= (cdr (assoc 0 (entget x))) "VERTEX")
             (setq n (1+ n)))
           (setq x (entnext x)))
         n)
        (T 0)))))

(defun jp:segment-at-point (e q / par segs s a b best)
  ;; Retorna o segmento da entidade que contem o ponto q.
  ;; q normalmente vem de getClosestPointTo e portanto esta sobre a curva.
  (setq par (jp:safe-call 'vlax-curve-getParamAtPoint (list e q))
        segs (jp:curve-segments e)
        best nil)
  (if (numberp par)
    (foreach s segs
      (setq a (jp:assocv 'a s)
            b (jp:assocv 'b s))
      (if (and (null best)
               (numberp a) (numberp b)
               (>= par (- a 1.0e-8))
               (<= par (+ b 1.0e-8)))
        (setq best s))))
  (if best best (car segs)))

(defun jp:piece-rank (e / nv ln)
  ;; Quanto MAIOR o rank, mais importante e preservar a porcao.
  ;; Vertices mandam; comprimento so desempata.
  (setq nv (jp:curve-vertex-count e)
        ln (jp:curve-length e))
  (list nv ln))

(defun jp:keep-topological-after-break (pieces / a b va vb la lb keep kill)
  ;; Regra v2.0 solicitada:
  ;;   1) apaga a porcao com MENOS vertices;
  ;;   2) empate de vertices -> apaga a porcao MENOR em comprimento;
  ;;   3) empate total -> preserva a primeira parte retornada pelo AutoCAD.
  (setq pieces (vl-remove-if-not 'jp:alive-p pieces))
  (cond
    ((null pieces) nil)
    ((= (length pieces) 1) (car pieces))
    (T
     (setq a (car pieces)
           b (cadr pieces)
           va (jp:curve-vertex-count a)
           vb (jp:curve-vertex-count b)
           la (jp:curve-length a)
           lb (jp:curve-length b))
     (cond
       ((> va vb) (setq keep a kill b))
       ((< va vb) (setq keep b kill a))
       ((>= la lb) (setq keep a kill b))
       (T (setq keep b kill a)))
     (if (and kill (jp:alive-p kill)) (jp:erase-ent kill))
     keep)))

(defun jp:trim-curve-to-node (e seg p eps / op pieces keep)
  ;; FILLET R=0 topologico: se o no cai no interior da entidade, divide e
  ;; elimina a porcao com menos vertices. Assim, detalhes curtos/pontas
  ;; parasitas sao podados antes do JOIN.
  (if (jp:entity-interior-xy-p e p eps)
    (progn
      (setq op (jp:seg-point3d-at-xy seg p)
            pieces (jp:break-at-point e op)
            keep (jp:keep-topological-after-break pieces))
      keep)
    e))

(defun jp:fix-xy-cross-once (ents tol zmode mode / iss s1 s2 e1 e2 p q1 q2 dz key z d1 d2 title eps k1 k2)
  (setq iss (jp:find-xy-cross ents tol))
  (if iss
    (progn
      (setq s1 (nth 0 iss) s2 (nth 1 iss) p (nth 2 iss)
            q1 (nth 3 iss) q2 (nth 4 iss) dz (nth 5 iss)
            key (nth 6 iss)
            e1 (jp:assocv 'ent s1) e2 (jp:assocv 'ent s2)
            z (jp:desired-z (jp:z q1) (jp:z q2) zmode)
            d1 (jp:seg-display-data s1)
            d2 (jp:seg-display-data s2)
            eps (jp:node-eps tol)
            title (strcat "FILLET 0 / INTERSECAO XY | podar lado com menos vertices | dz=" (rtos dz 2 4)))
      (if (jp:confirm-p mode title d1 d2 (jp:ptz p z) tol)
        (progn
          ;; Cada objeto que atravessa a intersecao e cortado no ponto;
          ;; fica o lado com MAIS vertices. Comprimento so desempata.
          (setq k1 (jp:trim-curve-to-node e1 s1 p eps)
                k2 (jp:trim-curve-to-node e2 s2 p eps))
          (if (and k1 k2 (jp:alive-p k1) (jp:alive-p k2))
            (progn
              (setq ents (jp:add-new-pieces ents (list k1 k2))
                    ents (vl-remove-if-not 'jp:alive-p ents))
              ;; Equaliza a cota exatamente no novo encontro.
              (jp:set-node-z-pieces (list k1 k2) p z (* eps 10.0))
              (list T title ents))
            (progn
              (jp:add-skip key)
              (list nil title (vl-remove-if-not 'jp:alive-p ents)))))
        (progn
          (if (not *JP-STOP*) (jp:add-skip key))
          (list 'SKIP title ents))))
    nil))


;;; =================================================================
;;; v1.5 - motor robusto de pontas / FILLET 0 multiplo
;;; =================================================================

(defun jp:valid-pt-p (p)
  (and (listp p)
       (numberp (car p))
       (numberp (cadr p))))

(defun jp:valid-num-p (x) (numberp x))

(defun jp:safe-call (fn args / r)
  (setq r (vl-catch-all-apply fn args))
  (if (vl-catch-all-error-p r) nil r))

(defun jp:closed-p (e / r)
  (if (jp:curve-p e)
    (progn
      (setq r (jp:safe-call 'vlax-curve-isClosed (list e)))
      (if r T nil))
    nil))


;; Redefinicoes defensivas: nenhuma entidade ruim pode derrubar a selecao inteira.
(defun jp:2d (p)
  (if (jp:valid-pt-p p) (list (car p) (cadr p) 0.0) nil))

(defun jp:dist2d (a b)
  (if (and (jp:valid-pt-p a) (jp:valid-pt-p b))
    (distance (list (car a) (cadr a) 0.0)
              (list (car b) (cadr b) 0.0))
    1.0e99))

(defun jp:z (p)
  (if (and (listp p) (numberp (caddr p))) (caddr p) 0.0))

(defun jp:get-terminal-data (e side / typ sp ep a b midp p0 p1 pm d0 d1 len kind)
  ;; Versao robusta. Se uma curva estiver degenerada/corrompida, retorna NIL
  ;; e ela simplesmente nao participa daquela etapa.
  (if (and (jp:curve-p e) (not (jp:closed-p e)))
    (progn
      (setq typ (jp:etype e)
            sp  (jp:safe-call 'vlax-curve-getStartParam (list e))
            ep  (jp:safe-call 'vlax-curve-getEndParam   (list e)))
      (if (and (numberp sp) (numberp ep) (> (- ep sp) *JP-EPS*))
        (progn
          (cond
            ((member typ '("LINE" "ARC"))
             (setq a sp b ep))
            ((= side "START")
             (setq a sp b (min ep (+ sp 1.0))))
            (T
             (setq a (max sp (- ep 1.0)) b ep)))
          (setq midp (/ (+ a b) 2.0)
                p0 (jp:safe-call 'vlax-curve-getPointAtParam (list e a))
                p1 (jp:safe-call 'vlax-curve-getPointAtParam (list e b))
                pm (jp:safe-call 'vlax-curve-getPointAtParam (list e midp)))
          (if (and (jp:valid-pt-p p0) (jp:valid-pt-p p1) (jp:valid-pt-p pm))
            (progn
              (setq d0 (jp:safe-call 'vlax-curve-getDistAtParam (list e a))
                    d1 (jp:safe-call 'vlax-curve-getDistAtParam (list e b)))
              (if (and (numberp d0) (numberp d1))
                (setq len (jp:abs (- d1 d0)))
                (setq len (distance p0 p1)))
              (setq kind (jp:seg-kind p0 pm p1))
              (if (= side "START")
                (list (cons 'ent e) (cons 'side side) (cons 'pt p0)
                      (cons 'inner p1) (cons 'len len) (cons 'kind kind)
                      (cons 'typ typ))
                (list (cons 'ent e) (cons 'side side) (cons 'pt p1)
                      (cons 'inner p0) (cons 'len len) (cons 'kind kind)
                      (cons 'typ typ))))))))))

(defun jp:get-endpoints (ents / out e d)
  (setq out '())
  (foreach e ents
    (if (and (jp:curve-p e) (not (jp:closed-p e)))
      (progn
        (setq d (jp:get-terminal-data e "START"))
        (if d (setq out (cons d out)))
        (setq d (jp:get-terminal-data e "END"))
        (if d (setq out (cons d out))))))
  (reverse out))

(defun jp:closest-point-xy (e p / s basez q)
  (setq s (jp:safe-call 'vlax-curve-getStartPoint (list e)))
  (if (and (jp:valid-pt-p s) (jp:valid-pt-p p))
    (progn
      (setq basez (jp:z s)
            q (jp:safe-call 'vlax-curve-getClosestPointTo
                            (list e (jp:ptz p basez))))
      (if (jp:valid-pt-p q) q nil))))

(defun jp:near-endpoint-p (e p tol / s en)
  (setq s  (jp:safe-call 'vlax-curve-getStartPoint (list e))
        en (jp:safe-call 'vlax-curve-getEndPoint   (list e)))
  (and (jp:valid-pt-p p)
       (jp:valid-pt-p s)
       (jp:valid-pt-p en)
       (or (<= (jp:dist2d p s) tol)
           (<= (jp:dist2d p en) tol))))

(defun jp:curve-local-data-at-point (e q / par sp ep p0 p1 pm a b d0 d1 kind len)
  (if (and (jp:curve-p e) (jp:valid-pt-p q))
    (progn
      (setq par (jp:safe-call 'vlax-curve-getParamAtPoint (list e q))
            sp  (jp:safe-call 'vlax-curve-getStartParam (list e))
            ep  (jp:safe-call 'vlax-curve-getEndParam   (list e)))
      (if (and (numberp par) (numberp sp) (numberp ep))
        (progn
          (if (member (jp:etype e) '("LINE" "ARC"))
            (setq a sp b ep)
            (progn
              (setq a (max sp (fix par))
                    b (min ep (+ a 1.0)))
              (if (<= (- b a) 1.0e-9)
                (setq a (max sp (- b 1.0))))))
          (setq p0 (jp:safe-call 'vlax-curve-getPointAtParam (list e a))
                p1 (jp:safe-call 'vlax-curve-getPointAtParam (list e b))
                pm (jp:safe-call 'vlax-curve-getPointAtParam (list e (/ (+ a b) 2.0))))
          (if (and (jp:valid-pt-p p0) (jp:valid-pt-p p1) (jp:valid-pt-p pm))
            (progn
              (setq d0 (jp:safe-call 'vlax-curve-getDistAtParam (list e a))
                    d1 (jp:safe-call 'vlax-curve-getDistAtParam (list e b)))
              (if (and (numberp d0) (numberp d1))
                (setq len (jp:abs (- d1 d0)))
                (setq len (distance p0 p1)))
              (setq kind (jp:seg-kind p0 pm p1))
              (list (cons 'ent e) (cons 'side "INTERIOR") (cons 'pt q)
                    (cons 'inner p0) (cons 'len len) (cons 'kind kind)
                    (cons 'typ (jp:etype e))))))))))

(defun jp:find-end-to-curve (ents tol / ends best bd d ce p q dxy dz key)
  (setq ends (jp:get-endpoints ents)
        best nil bd 1.0e99)
  (foreach d ends
    (setq p (jp:assocv 'pt d))
    (if (jp:valid-pt-p p)
      (foreach ce ents
        (if (and (jp:curve-p ce)
                 (/= ce (jp:assocv 'ent d)))
          (progn
            (setq q (jp:closest-point-xy ce p))
            (if (jp:valid-pt-p q)
              (progn
                (setq dxy (jp:dist2d p q)
                      dz  (jp:abs (- (jp:z p) (jp:z q)))
                      key (strcat "INT:" (jp:end-key d) "|" (jp:handle ce)))
                (if (and (not (jp:skipped-p key))
                         (<= dxy tol)
                         ;; Endpoint x endpoint e tratado em etapa propria.
                         (not (jp:near-endpoint-p ce q (max (* tol 0.10) 1.0e-7)))
                         (or (> dxy *JP-EPS*) (> dz *JP-EPS*)))
                  (if (< dxy bd)
                    (setq bd dxy best (list d ce q dxy dz)))))))))))
  best)

(defun jp:fix-end-to-curve-once (ents tol zmode mode / iss d ce q p z target local key title eps seg keep src)
  (setq iss (jp:find-end-to-curve ents tol))
  (if iss
    (progn
      (setq d (nth 0 iss) ce (nth 1 iss) q (nth 2 iss)
            p (jp:assocv 'pt d)
            src (jp:assocv 'ent d)
            z (jp:desired-z (jp:z p) (jp:z q) zmode)
            target (list (car q) (cadr q) z)
            local (jp:curve-local-data-at-point ce q)
            key (strcat "INT:" (jp:end-key d) "|" (jp:handle ce))
            eps (jp:node-eps tol)
            title (strcat "PONTA -> INTERIOR / FILLET R=0 TOPOLOGICO | dXY="
                          (rtos (nth 3 iss) 2 4)))
      (if (null local)
        (progn (jp:add-skip key) nil)
        (if (jp:confirm-p mode title d local (jp:mid p q) tol)
          (progn
            ;; Se o receptor passa pelo no, ele precisa deixar de ser um T:
            ;; divide no ponto e descarta o lado com MENOS vertices.
            (setq seg (jp:segment-at-point ce q)
                  keep (if seg (jp:trim-curve-to-node ce seg q eps) ce))
            (if (and keep (jp:alive-p keep) (jp:alive-p src))
              (progn
                (setq ents (jp:add-new-pieces ents (list keep src))
                      ents (vl-remove-if-not 'jp:alive-p ents))
                ;; Primeiro leva a ponta invasora ao no; depois grava a mesma
                ;; cota nas duas entidades sobreviventes.
                (jp:set-endpoint d target)
                (jp:set-node-z-pieces (list keep src) q z (* eps 10.0))
                (list T title ents))
              (progn
                (jp:add-skip key)
                (list nil title (vl-remove-if-not 'jp:alive-p ents)))))
          (progn
            (if (not *JP-STOP*) (jp:add-skip key))
            (list 'SKIP title ents)))))
    nil))

;;; --------- Intersecao de PONTAS: FILLET R=0 sem BREAK ------------

(defun jp:find-terminal-cross (ents tol / ends i j a b pa pb ia ib ip da db key score best bestscore)
  ;; Procura cruzamentos das RETAS dos segmentos terminais. Diferente do
  ;; endpoint-endpoint: aqui as pontas podem ter ULTRAPASSADO a intersecao.
  ;; Se cada ponta esta a ate TOL do no, move as duas ao no e remove as pontas.
  (setq ends (jp:get-endpoints ents)
        i 0 best nil bestscore 1.0e99)
  (while (< i (length ends))
    (setq a (nth i ends) j (1+ i))
    (while (< j (length ends))
      (setq b (nth j ends))
      (if (and (/= (jp:assocv 'ent a) (jp:assocv 'ent b))
               (= (jp:assocv 'kind a) "LINE")
               (= (jp:assocv 'kind b) "LINE"))
        (progn
          (setq pa (jp:assocv 'pt a) pb (jp:assocv 'pt b)
                ia (jp:assocv 'inner a) ib (jp:assocv 'inner b))
          (if (and (jp:valid-pt-p pa) (jp:valid-pt-p pb)
                   (jp:valid-pt-p ia) (jp:valid-pt-p ib))
            (progn
              (setq ip (jp:inf-line-intersection2d ia pa ib pb))
              (if (jp:valid-pt-p ip)
                (progn
                  (setq da (jp:dist2d pa ip)
                        db (jp:dist2d pb ip)
                        key (jp:pair-key "TCROSS" a b)
                        score (+ da db))
                  (if (and (not (jp:skipped-p key))
                           (<= da tol) (<= db tol)
                           ;; Evita repetir um no ja perfeito.
                           (or (> da *JP-EPS*) (> db *JP-EPS*)
                               (> (jp:abs (- (jp:z pa) (jp:z pb))) *JP-EPS*))
                           (< score bestscore))
                    (setq bestscore score
                          best (list a b ip da db key)))))))))
      (setq j (1+ j)))
    (setq i (1+ i)))
  best)

(defun jp:fix-terminal-cross-once (ents tol zmode mode / iss a b ip z key title)
  (setq iss (jp:find-terminal-cross ents tol))
  (if iss
    (progn
      (setq a (nth 0 iss) b (nth 1 iss) ip (nth 2 iss)
            key (nth 5 iss)
            z (jp:desired-z (jp:z (jp:assocv 'pt a))
                            (jp:z (jp:assocv 'pt b)) zmode)
            title (strcat "FILLET R=0 / RETIRAR PONTAS | A="
                          (rtos (nth 3 iss) 2 4) " B="
                          (rtos (nth 4 iss) 2 4)))
      (if (jp:confirm-p mode title a b (jp:ptz ip z) tol)
        (progn
          ;; Nao quebra a entidade. Apenas leva os vertices terminais ao no.
          ;; Isso e o comportamento desejado para uma cadeia a ser JOINada.
          (jp:set-endpoint a (jp:ptz ip z))
          (jp:set-endpoint b (jp:ptz ip z))
          (list T title))
        (progn
          (if (not *JP-STOP*) (jp:add-skip key))
          (list 'SKIP title))))
    nil))

(defun jp:line-horizontal-p (e / a b)
  (setq a (jp:safe-call 'vlax-curve-getStartPoint (list e))
        b (jp:safe-call 'vlax-curve-getEndPoint   (list e)))
  (and (jp:valid-pt-p a) (jp:valid-pt-p b)
       (<= (jp:abs (- (jp:z a) (jp:z b))) 1.0e-7)))

(defun jp:pedit-joinable-p (e / typ)
  ;; LINE inclinada em Z NAO deve ser convertida em LWPOLYLINE.
  ;; Ela fica para o JOIN nativo, que pode formar 3D POLYLINE.
  (and (jp:alive-p e)
       (not (jp:closed-p e))
       (setq typ (jp:etype e))
       (or (and (= typ "LINE") (jp:line-horizontal-p e))
           (= typ "ARC")
           (= typ "LWPOLYLINE")
           (and (= typ "POLYLINE") (not (jp:poly3d-p e))))))

(defun jp:run-pedit-join (ents tol / ss oldpea ret ret2 ok allss)
  ;; 1) PEDIT para geometria 2D, usando Jointype Extend.
  ;; 2) JOIN nativo depois, inclusive para LINEs inclinadas/3D.
  (setq ss (jp:make-pedit-ss ents)
        oldpea (getvar "PEDITACCEPT")
        ok nil)
  (if (> (sslength ss) 1)
    (progn
      (setvar "PEDITACCEPT" 1)
      (setq ret
        (vl-catch-all-apply
          '(lambda ()
             (command-s "_.PEDIT" "_M" ss ""
                        "_J" "_J" "_E" tol ""))))
      (if (not (vl-catch-all-error-p ret)) (setq ok T))))
  (setvar "PEDITACCEPT" oldpea)

  ;; Reconstroi o conjunto porque PEDIT pode ter absorvido entidades.
  (setq allss (jp:make-ss (vl-remove-if-not 'jp:alive-p ents)))
  (if (> (sslength allss) 1)
    (progn
      (setq ret2
        (vl-catch-all-apply
          '(lambda () (command-s "_.JOIN" allss ""))))
      (if (not (vl-catch-all-error-p ret2)) (setq ok T))))
  ok)

(defun jp:get-tol (/ v)
  (initget 6)
  (setq v (getdist (strcat "\nTolerancia XY <" (rtos *JP-TOL* 2 4) ">: ")))
  (if v (setq *JP-TOL* v))
  *JP-TOL*)

(defun jp:get-zmode (/ z)
  (initget "Superior Inferior")
  (setq z (getkword "\nCota que deve prevalecer [Superior/Inferior] <Superior>: "))
  (if z z "Superior"))

(defun jp:ss->list (ss / i out)
  (setq i 0 out '())
  (while (< i (sslength ss))
    (setq out (cons (ssname ss i) out)
          i (1+ i)))
  (reverse out))

(defun jp:get-selection (/ ss)
  ;; PICKFIRST + selecao multipla normal.
  (setq ss (ssget "_I" '((0 . "LINE,ARC,LWPOLYLINE,POLYLINE"))))
  (if (null ss)
    (setq ss (ssget '((0 . "LINE,ARC,LWPOLYLINE,POLYLINE")))))
  ss)

(defun jp:usable-open-p (e)
  (and (jp:curve-p e)
       (not (jp:closed-p e))
       (or (jp:get-terminal-data e "START")
           (jp:get-terminal-data e "END"))))

(defun jp:main (mode / *error* oldcmdecho oldpea ss rawents ents ignored tol zmode pass res
                          cEnd cInt cOv cCross cXy consolidated overkilled phase undo-open closedcnt invalidcnt e)
  (vl-load-com)
  (setq oldcmdecho (getvar "CMDECHO")
        oldpea (getvar "PEDITACCEPT")
        *JP-STOP* nil
        *JP-SKIP* '()
        phase "inicializacao"
        undo-open nil)

  (defun *error* (msg)
    (redraw)
    (setvar "CMDECHO" oldcmdecho)
    (setvar "PEDITACCEPT" oldpea)
    ;; UNDO nativo evita dependencia do namespace COM do documento.
    (if undo-open
      (progn
        (command-s "_.UNDO" "_End")
        (setq undo-open nil)))
    (if (and msg (not (wcmatch (strcase msg) "*CANCEL*,*QUIT*")))
      (princ (strcat "\n[JPLUS] Erro na etapa [" phase "]: " msg)))
    (princ))

  (setq ss (jp:get-selection))
  (if (null ss)
    (princ "\n[JPLUS] Nenhum objeto valido selecionado.")
    (progn
      (setq rawents (jp:ss->list ss)
            ents (vl-remove-if-not 'jp:usable-open-p rawents)
            ignored (- (length rawents) (length ents))
            tol (jp:get-tol)
            zmode (jp:get-zmode)
            cEnd 0 cInt 0 cOv 0 cCross 0 cXy 0
            consolidated nil overkilled nil
            closedcnt 0 invalidcnt 0)

      ;; Classifica os ignorados sem chamar uma polilinha fechada de "degenerada".
      (foreach e rawents
        (if (not (member e ents))
          (if (jp:closed-p e)
            (setq closedcnt (1+ closedcnt))
            (setq invalidcnt (1+ invalidcnt)))))

      (princ (strcat "\n[JPLUS] Objetos selecionados: " (itoa (length rawents))))
      (if (> ignored 0)
        (princ
          (strcat " | ignorados: " (itoa ignored)
                  " (fechados: " (itoa closedcnt)
                  " / sem extremidade valida: " (itoa invalidcnt) ")")))

      (if (< (length ents) 2)
        (princ "\n[JPLUS] Menos de 2 entidades abertas utilizaveis.")
        (progn
          (setvar "CMDECHO" 0)
          ;; Grupo UNDO nativo: mais estavel entre namespaces/documentos do Civil 3D.
          (command-s "_.UNDO" "_Begin")
          (setq undo-open T)

          ;; 1. Pontas proximas: FILLET 0 para nao paralelas; hierarquia para paralelas.
          (setq phase "endpoint-endpoint" pass 0)
          (while (and (< pass *JP-MAXPASS*) (not *JP-STOP*)
                      (setq res (jp:fix-endpair-once ents tol zmode mode)))
            (if (eq (car res) T) (setq cEnd (1+ cEnd)))
            (setq pass (1+ pass)))

          ;; 2. Intersecoes XY reais (inclusive no MEIO de polylines):
          ;; divide no no e poda o lado com MENOS VERTICES. Esse e o caso
          ;; equivalente ao FILLET R=0 que antes podia deixar uma ponta/T.
          (setq phase "fillet-r0-intersecoes-xy" pass 0)
          (while (and (< pass *JP-MAXPASS*) (not *JP-STOP*)
                      (setq res (jp:fix-xy-cross-once ents tol zmode mode)))
            (if (eq (car res) T) (setq cXy (1+ cXy)))
            (if (and (listp res) (nth 2 res))
              (setq ents (nth 2 res)))
            (setq pass (1+ pass)))

          ;; 3. Pontas que ultrapassaram o cruzamento: remove somente a ponta,
          ;; sem BREAK e sem apagar metade da polilinha.
          (setq phase "fillet-r0-pontas" pass 0)
          (while (and (< pass *JP-MAXPASS*) (not *JP-STOP*)
                      (setq res (jp:fix-terminal-cross-once ents tol zmode mode)))
            (if (eq (car res) T) (setq cCross (1+ cCross)))
            (setq pass (1+ pass)))

          ;; 4. Endpoint perto do interior: vira FILLET R=0 topologico.
          ;; O receptor e podado no no (menos vertices fora) para permitir JOIN.
          (setq phase "endpoint-interior-fillet" pass 0)
          (while (and (< pass *JP-MAXPASS*) (not *JP-STOP*)
                      (setq res (jp:fix-end-to-curve-once ents tol zmode mode)))
            (if (eq (car res) T) (setq cInt (1+ cInt)))
            (if (and (listp res) (nth 2 res))
              (setq ents (nth 2 res)))
            (setq pass (1+ pass)))

          ;; 5. Overlap linear explicito.
          (setq phase "overlap" pass 0)
          (while (and (< pass *JP-MAXPASS*) (not *JP-STOP*)
                      (setq res (jp:fix-line-overlap-once ents tol zmode mode)))
            (if (eq (car res) T) (setq cOv (1+ cOv)))
            (setq pass (1+ pass)))

          ;; 6. Repassa pontas depois dos trims.
          (setq phase "revisao-final-de-pontas" pass 0
                ents (vl-remove-if-not 'jp:alive-p ents))
          (while (and (< pass *JP-MAXPASS*) (not *JP-STOP*)
                      (setq res (jp:fix-endpair-once ents tol zmode mode)))
            (if (eq (car res) T) (setq cEnd (1+ cEnd)))
            (setq pass (1+ pass)))

          ;; 7. Limpa duplicatas/overlaps residuais.
          (setq phase "overkill"
                ents (vl-remove-if-not 'jp:alive-p ents))
          (if (and (not *JP-STOP*) (> (length ents) 1))
            (setq overkilled (jp:run-overkill ents tol mode)))

          ;; 8. JOIN sempre e a ultima etapa.
          (setq phase "join-final"
                ents (vl-remove-if-not 'jp:alive-p ents))
          (if (and (not *JP-STOP*) (> (length ents) 1))
            (setq consolidated (jp:run-pedit-join ents tol)))

          (if undo-open
            (progn
              (command-s "_.UNDO" "_End")
              (setq undo-open nil)))
          (setvar "CMDECHO" oldcmdecho)
          (setvar "PEDITACCEPT" oldpea)
          (redraw)

          (princ
            (strcat
              "\n[JPLUS] Concluido."
              "\n  Objetos de entrada: " (itoa (sslength ss))
              (if (> ignored 0)
                (strcat "\n  Ignorados: " (itoa ignored)
                        " (fechados: " (itoa closedcnt)
                        " / sem extremidade valida: " (itoa invalidcnt) ")")
                "")
              "\n  Endpoint-endpoint corrigidos: " (itoa cEnd)
              "\n  Intersecoes XY podadas por vertices: " (itoa cXy)
              "\n  Pontas removidas por FILLET R=0: " (itoa cCross)
              "\n  Endpoint-interior corrigidos: " (itoa cInt)
              "\n  Overlaps LINE x LINE: " (itoa cOv)
              (if overkilled "\n  Limpeza complementar executada." "")
              (if consolidated "\n  JOIN/PEDIT final executado." "\n  Nenhuma consolidacao final foi possivel.")
              "\n  UNDO: uma unica operacao desfaz toda a rotina."))))))
  (princ))


;;; ---------------------- LIGAR POR LINHAS -------------------------
;;; JPLUSL nao move/apara as geometrias existentes. Ele cria LINEs
;;; entre extremidades livres de componentes separados. A cada uniao,
;;; os dois endpoints usados deixam de ser livres; assim a topologia
;;; resultante permanece uma cadeia, sem criar ramificacoes.

(defun jp:get-bridge-max (/ v)
  (initget 6)
  (setq v
    (getdist
      (strcat "\nDistancia maxima para criar cada linha de ligacao <"
              (rtos *JP-BRIDGE-MAX* 2 4) ">: ")))
  (if v (setq *JP-BRIDGE-MAX* v))
  *JP-BRIDGE-MAX*)

(defun jp:component (e / d1 d2)
  (setq d1 (jp:get-terminal-data e "START")
        d2 (jp:get-terminal-data e "END"))
  (if (and d1 d2)
    (list (cons 'ents (list e))
          (cons 'end1 d1)
          (cons 'end2 d2))))

(defun jp:comp-end (c which)
  (if (= which 0) (jp:assocv 'end1 c) (jp:assocv 'end2 c)))

(defun jp:comp-other-end (c which)
  (if (= which 0) (jp:assocv 'end2 c) (jp:assocv 'end1 c)))

(defun jp:pt3dist-safe (p q)
  (if (and p q) (distance p q) 1.0e99))

(defun jp:best-component-link (comps maxgap / i j si sj ci cj ei ej p1 p2 d best bestd)
  ;; Procura a menor distancia 3D entre endpoints livres de COMPONENTES
  ;; diferentes. A restricao de endpoints livres impede criar um Y/T.
  (setq i 0 best nil bestd 1.0e99)
  (while (< i (length comps))
    (setq ci (nth i comps)
          j (1+ i))
    (while (< j (length comps))
      (setq cj (nth j comps)
            si 0)
      (while (< si 2)
        (setq sj 0)
        (while (< sj 2)
          (setq ei (jp:comp-end ci si)
                ej (jp:comp-end cj sj)
                p1 (if ei (jp:assocv 'pt ei) nil)
                p2 (if ej (jp:assocv 'pt ej) nil)
                d  (jp:pt3dist-safe p1 p2))
          (if (and (< d bestd) (<= d (+ maxgap *JP-EPS*)))
            (setq bestd d
                  best (list i si j sj d ei ej)))
          (setq sj (1+ sj)))
        (setq si (1+ si)))
      (setq j (1+ j)))
    (setq i (1+ i)))
  best)

(defun jp:remove-two-indices (lst i j / out k x)
  (setq out '() k 0)
  (foreach x lst
    (if (and (/= k i) (/= k j))
      (setq out (cons x out)))
    (setq k (1+ k)))
  (reverse out))

(defun jp:entity-layer (e / ed lay)
  (setq ed (if (jp:alive-p e) (entget e) nil)
        lay (if ed (cdr (assoc 8 ed)) nil))
  (if lay lay (getvar "CLAYER")))

(defun jp:add-bridge-line (p1 p2 refent / doc space obj lay ret)
  ;; ActiveX evita depender de UCS/OSNAP/comando LINE.
  (setq doc (vla-get-ActiveDocument (vlax-get-acad-object))
        space (if (= (getvar "CVPORT") 1)
                (vla-get-PaperSpace doc)
                (vla-get-ModelSpace doc))
        lay (jp:entity-layer refent)
        ret (vl-catch-all-apply
              'vla-AddLine
              (list space (vlax-3d-point p1) (vlax-3d-point p2))))
  (if (vl-catch-all-error-p ret)
    nil
    (progn
      (setq obj ret)
      (if lay (vl-catch-all-apply 'vla-put-Layer (list obj lay)))
      (vlax-vla-object->ename obj))))

(defun jp:merge-components (comps link / i si j sj ci cj ei ej oi oj p1 p2 d line newents newc rest refent)
  (setq i  (nth 0 link)
        si (nth 1 link)
        j  (nth 2 link)
        sj (nth 3 link)
        d  (nth 4 link)
        ei (nth 5 link)
        ej (nth 6 link)
        ci (nth i comps)
        cj (nth j comps)
        oi (jp:comp-other-end ci si)
        oj (jp:comp-other-end cj sj)
        p1 (jp:assocv 'pt ei)
        p2 (jp:assocv 'pt ej)
        line nil)

  ;; Se os endpoints ja forem exatamente coincidentes em 3D, apenas
  ;; funde os componentes logicos. Caso contrario cria uma LINE real.
  (if (> d *JP-EPS*)
    (progn
      (setq refent (jp:assocv 'ent ei)
            line (jp:add-bridge-line p1 p2 refent))))

  (if (or (<= d *JP-EPS*) line)
    (progn
      (setq newents
        (append (jp:assocv 'ents ci)
                (jp:assocv 'ents cj)
                (if line (list line) '())))
      (setq newc
        (list (cons 'ents newents)
              (cons 'end1 oi)
              (cons 'end2 oj)))
      (setq rest (jp:remove-two-indices comps i j))
      (list (cons 'components (cons newc rest))
            (cons 'line line)
            (cons 'distance d)))
    nil))


;;; ------------------ consolidacao FORCADA do JPLUSL ----------------

(defun jp:uniq-alive (ents / out e)
  (setq out '())
  (foreach e ents
    (if (and (jp:alive-p e) (not (member e out)))
      (setq out (cons e out))))
  (reverse out))

(defun jp:new-curves-after (mark / out e)
  ;; Captura entidades novas que PEDIT/JOIN eventualmente recriem.
  (setq out '()
        e (if mark (entnext mark) (entnext)))
  (while e
    (if (jp:curve-p e) (setq out (cons e out)))
    (setq e (entnext e)))
  (reverse out))

(defun jp:run-native-join-all (ents / ss ret)
  ;; No JOIN atual do AutoCAD, fornecer todo o selection set no primeiro
  ;; prompt equivale a "multiple objects to join at once".
  (setq ss (jp:make-ss (jp:uniq-alive ents)))
  (if (> (sslength ss) 1)
    (progn
      (setq ret
        (vl-catch-all-apply
          '(lambda () (command-s "_.JOIN" ss ""))))
      (not (vl-catch-all-error-p ret)))
    nil))

(defun jp:force-chain-join (ents tol / pool pass before after mark news progress done)
  ;; O JPLUSL constroi uma cadeia sem ramificacoes. Aqui insistimos no
  ;; consolidado final: JOIN -> PEDIT/JOIN -> JOIN, repetindo enquanto o
  ;; numero de entidades cai. Isso resolve casos em que uma primeira
  ;; conversao para POLYLINE ainda deixa duas cadeias prontas para unir.
  (setq pool (jp:uniq-alive ents)
        pass 0
        done nil
        progress T)

  (while (and progress (< pass 12) (> (length (jp:uniq-alive pool)) 1))
    (setq pass (1+ pass)
          before (length (jp:uniq-alive pool))
          mark (entlast))

    ;; Primeiro tenta o JOIN nativo, que e muito bom quando as pontas ja
    ;; coincidem exatamente (caso tipico depois das LINEs de ligacao).
    (jp:run-native-join-all pool)
    (setq news (jp:new-curves-after mark))
    (foreach e news (if (not (member e pool)) (setq pool (cons e pool))))

    ;; Em seguida converte/consolida a geometria 2D via PEDIT Multiple.
    (if (> (length (jp:uniq-alive pool)) 1)
      (progn
        (setq mark (entlast))
        (jp:run-pedit-join pool tol)
        (setq news (jp:new-curves-after mark))
        (foreach e news (if (not (member e pool)) (setq pool (cons e pool))))))

    ;; Uma ultima passada de JOIN absorve LINEs de ponte ou partes que o
    ;; PEDIT acabou de converter e deixou prontas para a uniao.
    (if (> (length (jp:uniq-alive pool)) 1)
      (progn
        (setq mark (entlast))
        (jp:run-native-join-all pool)
        (setq news (jp:new-curves-after mark))
        (foreach e news (if (not (member e pool)) (setq pool (cons e pool))))))

    (setq pool (jp:uniq-alive pool)
          after (length pool)
          progress (< after before)))

  (setq done (= (length (jp:uniq-alive pool)) 1))
  (list (cons 'ok done)
        (cons 'ents (jp:uniq-alive pool))
        (cons 'passes pass)))

(defun jp:bridge-summary (nlines comps consolidated finalcount passes)
  (princ
    (strcat
      "\n[JPLUSL] Concluido."
      "\n  Linhas de ligacao criadas: " (itoa nlines)
      "\n  Componentes restantes antes do JOIN: " (itoa (length comps))
      "\n  Entidades restantes apos JOIN: " (itoa finalcount)
      "\n  Passagens de JOIN/PEDIT: " (itoa passes)
      (if consolidated
        "\n  OK: cadeia consolidada em UMA unica entidade."
        "\n  ATENCAO: ainda restou mais de uma entidade; normalmente indica geometria nao coplanar/3D ou ramificacao.")
      "\n  UNDO: uma unica operacao desfaz toda a rotina.")))

(defun jp:bridge-main (/ *error* oldcmdecho oldpea ss rawents ents ignored maxgap comps e c link merged
                          nlines allents consolidated phase bridgefail joinres finalents finalcount joinpasses
                          undo-open closedcnt invalidcnt)
  (vl-load-com)
  (setq oldcmdecho (getvar "CMDECHO")
        oldpea (getvar "PEDITACCEPT")
        phase "selecao"
        undo-open nil)

  (defun *error* (msg)
    (setvar "CMDECHO" oldcmdecho)
    (setvar "PEDITACCEPT" oldpea)
    (if undo-open
      (progn
        (command-s "_.UNDO" "_End")
        (setq undo-open nil)))
    (if (and msg (not (wcmatch (strcase msg) "*CANCEL*,*QUIT*")))
      (princ (strcat "\n[JPLUSL] Erro na etapa [" phase "]: " msg)))
    (princ))

  (setq ss (jp:get-selection))
  (if (null ss)
    (princ "\n[JPLUSL] Nenhum objeto valido selecionado.")
    (progn
      (setq rawents (jp:ss->list ss)
            ents (vl-remove-if-not 'jp:usable-open-p rawents)
            ignored (- (length rawents) (length ents))
            closedcnt 0 invalidcnt 0)
      (foreach e rawents
        (if (not (member e ents))
          (if (jp:closed-p e)
            (setq closedcnt (1+ closedcnt))
            (setq invalidcnt (1+ invalidcnt)))))
      (if (< (length ents) 2)
        (princ "\n[JPLUSL] Selecione pelo menos 2 entidades abertas utilizaveis.")
        (progn
          (setq maxgap (jp:get-bridge-max)
                comps '()
                nlines 0
                consolidated nil)
          (foreach e ents
            (setq c (jp:component e))
            (if c (setq comps (cons c comps))))
          (setq comps (reverse comps))

          (princ (strcat "\n[JPLUSL] Objetos selecionados: " (itoa (length rawents))))
          (if (> ignored 0)
            (princ
              (strcat " | ignorados: " (itoa ignored)
                      " (fechados: " (itoa closedcnt)
                      " / sem extremidade valida: " (itoa invalidcnt) ")")))

          (setvar "CMDECHO" 0)
          (command-s "_.UNDO" "_Begin")
          (setq undo-open T)
          (setq phase "criacao-das-ligacoes"
                bridgefail nil)

          ;; Cada iteracao reduz em 1 o numero de componentes. Como os
          ;; endpoints usados deixam de ser livres, o resultado e linear.
          (while (and (not bridgefail)
                      (> (length comps) 1)
                      (setq link (jp:best-component-link comps maxgap)))
            (setq merged (jp:merge-components comps link))
            (if merged
              (progn
                (setq comps (jp:assocv 'components merged))
                (if (jp:assocv 'line merged)
                  (setq nlines (1+ nlines))))
              ;; Falha ao criar LINE: encerra para nao entrar em loop.
              (setq bridgefail T)))

          (setq phase "join-final"
                allents '())
          (foreach e ents
            (if (jp:alive-p e) (setq allents (cons e allents))))
          ;; Inclui todas as linhas criadas, mesmo se a distancia maxima
          ;; deixar mais de um componente sem conexao.
          (foreach c comps
            (foreach e (jp:assocv 'ents c)
              (if (and (jp:alive-p e) (not (member e allents)))
                (setq allents (cons e allents)))))

          ;; Forca o JOIN final ate uma unica entidade quando a cadeia
          ;; realmente for geometricamente univel. Usa a distancia maxima
          ;; apenas para CRIAR pontes; aqui a tolerancia de JOIN fica no
          ;; epsilon porque as novas LINEs nascem exatamente nos endpoints.
          (setq joinres (jp:force-chain-join allents *JP-EPS*)
                consolidated (jp:assocv 'ok joinres)
                finalents (jp:assocv 'ents joinres)
                finalcount (length finalents)
                joinpasses (jp:assocv 'passes joinres))

          (if undo-open
            (progn
              (command-s "_.UNDO" "_End")
              (setq undo-open nil)))
          (setvar "CMDECHO" oldcmdecho)
          (setvar "PEDITACCEPT" oldpea)
          (jp:bridge-summary nlines comps consolidated finalcount joinpasses)))))
  (princ))




;;; =================================================================
;;; v1.9 - JOIN final par-a-par
;;; =================================================================
;;; O PEDIT/JOIN com toda a selecao de uma vez pode falhar quando existem
;;; varias cadeias desconectadas, tipos diferentes ou um unico objeto que o
;;; AutoCAD recusa. Nesta versao o JOIN final trabalha SOMENTE com pares de
;;; entidades cujas extremidades estao proximas. Depois de cada uniao a lista
;;; e reconstruida e o processo recomeca. Assim uma cadeia ruim nao impede as
;;; demais de serem consolidadas.

(setq *JP-CURRENT-ZMODE* "Superior")
(setq *JP-LAST-JOINED* 0)
(setq *JP-LAST-JOIN-FAILED* '())

(defun jp:get-zmode (/ z)
  (initget "Superior Inferior")
  (setq z (getkword "\nCota que deve prevalecer [Superior/Inferior] <Superior>: "))
  (if (null z) (setq z "Superior"))
  (setq *JP-CURRENT-ZMODE* z)
  z)

(defun jp:pool-signature (ents / hs e)
  (setq hs '())
  (foreach e (jp:uniq-alive ents)
    (setq hs (cons (jp:handle e) hs)))
  (vl-sort hs 'jp:str-less-p))

(defun jp:join-candidate (ents tol failed / ends i j a b pa pb dxy dz score best bestscore key)
  ;; Inclui inclusive endpoints JA perfeitamente coincidentes. Esse e o caso
  ;; que as versoes anteriores entregavam diretamente ao PEDIT global.
  (setq ends (jp:get-endpoints (jp:uniq-alive ents))
        i 0
        best nil
        bestscore 1.0e99)
  (while (< i (length ends))
    (setq a (nth i ends)
          j (1+ i))
    (while (< j (length ends))
      (setq b (nth j ends))
      (if (/= (jp:assocv 'ent a) (jp:assocv 'ent b))
        (progn
          (setq pa (jp:assocv 'pt a)
                pb (jp:assocv 'pt b))
          (if (and (jp:valid-pt-p pa) (jp:valid-pt-p pb))
            (progn
              (setq dxy (jp:dist2d pa pb)
                    dz (jp:abs (- (jp:z pa) (jp:z pb)))
                    key (jp:pair-key "PJOIN" a b)
                    ;; XY manda. DZ so desempata; a escolha Superior/Inferior
                    ;; sera aplicada antes da tentativa de JOIN.
                    score (+ dxy (* (min dz 1000000.0) 1.0e-9)))
              (if (and (<= dxy tol)
                       (not (member key failed))
                       (< score bestscore))
                (setq bestscore score
                      best (list a b dxy dz key)))))))
      (setq j (1+ j)))
    (setq i (1+ i)))
  best)

(defun jp:canonicalize-join-pair (a b / mv fx pfx z target)
  ;; Regrava os dois endpoints no MESMO ponto WCS antes do JOIN. Isso remove
  ;; pequenas diferencas numericas que parecem coincidencia na tela/ID.
  ;; Mantem a hierarquia ARC > LINE / segmento maior > menor para o XY e a
  ;; regra Superior/Inferior para Z.
  (setq mv (jp:choose-mover a b)
        fx (if (= mv a) b a)
        pfx (jp:assocv 'pt fx)
        z (jp:desired-z (jp:z (jp:assocv 'pt a))
                        (jp:z (jp:assocv 'pt b))
                        *JP-CURRENT-ZMODE*)
        target (jp:ptz pfx z))
  ;; Regravar tambem a referencia e intencional: normaliza o endpoint mesmo
  ;; quando dXY e dZ sao numericamente zero.
  (and (jp:set-endpoint fx target)
       (jp:set-endpoint mv target)))

(defun jp:run-native-join-two (e1 e2 / ret)
  (if (and (jp:alive-p e1) (jp:alive-p e2))
    (progn
      (setq ret
        (vl-catch-all-apply
          '(lambda () (command-s "_.JOIN" e1 e2 ""))))
      (not (vl-catch-all-error-p ret)))
    nil))

(defun jp:run-pedit-two (e1 e2 tol / ss oldpea ret)
  ;; PEDIT somente no PAR atual. Um objeto incompativel nao contamina as
  ;; outras dezenas de entidades selecionadas.
  (if (and (jp:alive-p e1) (jp:alive-p e2)
           (jp:pedit-joinable-p e1) (jp:pedit-joinable-p e2))
    (progn
      (setq ss (ssadd)
            oldpea (getvar "PEDITACCEPT"))
      (ssadd e1 ss)
      (ssadd e2 ss)
      (setvar "PEDITACCEPT" 1)
      (setq ret
        (vl-catch-all-apply
          '(lambda ()
             (command-s "_.PEDIT" "_M" ss ""
                        "_J" "_J" "_E" tol ""))))
      (setvar "PEDITACCEPT" oldpea)
      (not (vl-catch-all-error-p ret)))
    nil))

(defun jp:refresh-pool (pool mark / news e)
  (setq news (jp:new-curves-after mark))
  (foreach e news
    (if (not (member e pool))
      (setq pool (cons e pool))))
  (jp:uniq-alive pool))

(defun jp:join-one-pair-cycle (pool cand tol / a b e1 e2 before sig0 mark after sig1)
  (setq a (nth 0 cand)
        b (nth 1 cand)
        e1 (jp:assocv 'ent a)
        e2 (jp:assocv 'ent b)
        pool (jp:uniq-alive pool)
        before (length pool)
        sig0 (jp:pool-signature pool))

  ;; Primeiro transforma as duas extremidades em uma coordenada realmente
  ;; identica, inclusive em Z.
  (jp:canonicalize-join-pair a b)

  ;; 1) JOIN normal com A como fonte.
  (setq mark (entlast))
  (jp:run-native-join-two e1 e2)
  (setq pool (jp:refresh-pool pool mark)
        after (length pool)
        sig1 (jp:pool-signature pool))

  ;; 2) Alguns tipos so unem quando a outra entidade e a fonte.
  (if (and (= after before) (equal sig1 sig0) (jp:alive-p e1) (jp:alive-p e2))
    (progn
      (setq mark (entlast))
      (jp:run-native-join-two e2 e1)
      (setq pool (jp:refresh-pool pool mark)
            after (length pool)
            sig1 (jp:pool-signature pool))))

  ;; 3) Fallback PEDIT somente para esse par.
  (if (and (= after before) (equal sig1 sig0) (jp:alive-p e1) (jp:alive-p e2))
    (progn
      (setq mark (entlast))
      (jp:run-pedit-two e1 e2 tol)
      (setq pool (jp:refresh-pool pool mark)
            after (length pool)
            sig1 (jp:pool-signature pool))))

  ;; 4) Se o PEDIT apenas converteu as entidades sem junta-las, o loop
  ;; externo as reencontra com os novos handles. Se nao converteu, ainda
  ;; fazemos uma ultima tentativa de JOIN.
  (if (and (= after before) (equal sig1 sig0) (jp:alive-p e1) (jp:alive-p e2))
    (progn
      (setq mark (entlast))
      (jp:run-native-join-two e1 e2)
      (setq pool (jp:refresh-pool pool mark)
            after (length pool)
            sig1 (jp:pool-signature pool))))

  (cond
    ((< after before)
     (list (cons 'status "JOINED")
           (cons 'reduced (- before after))
           (cons 'pool pool)))
    ((not (equal sig1 sig0))
     (list (cons 'status "CHANGED")
           (cons 'reduced 0)
           (cons 'pool pool)))
    (T
     (list (cons 'status "FAILED")
           (cons 'reduced 0)
           (cons 'pool pool)))))

(defun jp:run-pedit-join (ents tol / pool failed pass cand res status reduced joined stubborn maxpass a b)
  ;; Redefinicao v1.9: NAO envia mais toda a selecao ao PEDIT/JOIN de uma vez.
  ;; Cada cadeia e montada progressivamente por pares de endpoints.
  (setq pool (jp:uniq-alive ents)
        failed '()
        pass 0
        joined 0
        stubborn '()
        maxpass (max 50 (* 12 (length pool))))

  (while (and (< pass maxpass)
              (setq cand (jp:join-candidate pool tol failed)))
    (setq pass (1+ pass)
          res (jp:join-one-pair-cycle pool cand tol)
          status (jp:assocv 'status res)
          reduced (jp:assocv 'reduced res)
          pool (jp:assocv 'pool res))
    (cond
      ((= status "JOINED")
       (setq joined (+ joined reduced)))
      ((= status "CHANGED")
       ;; Conversao LINE/ARC -> POLYLINE sem reducao. Nao marca como falha;
       ;; a proxima iteracao tenta novamente com a entidade convertida.
       nil)
      (T
       (setq failed (cons (nth 4 cand) failed)
             stubborn (cons cand stubborn)))))

  (setq *JP-LAST-JOINED* joined
        *JP-LAST-JOIN-FAILED* (reverse stubborn))

  (princ
    (strcat
      "\n[JPLUS] JOIN par-a-par: " (itoa joined) " uniao(oes) confirmada(s)"
      (if (> (length stubborn) 0)
        (strcat " | pares que o AutoCAD recusou: " (itoa (length stubborn)))
        "")))

  (> joined 0))

(defun c:JPLUSFALHAS (/ x a b)
  ;; Mostra os pares que estavam geometricamente prontos, mas que nem JOIN
  ;; nem PEDIT conseguiram consolidar na ultima execucao.
  (if (null *JP-LAST-JOIN-FAILED*)
    (princ "\n[JPLUS] Nenhum par recusado registrado na ultima execucao.")
    (progn
      (princ (strcat "\n[JPLUS] Pares recusados: " (itoa (length *JP-LAST-JOIN-FAILED*))))
      (foreach x *JP-LAST-JOIN-FAILED*
        (setq a (nth 0 x)
              b (nth 1 x))
        (princ
          (strcat
            "\n  " (jp:handle (jp:assocv 'ent a)) " [" (jp:assocv 'typ a) "]"
            " <-> " (jp:handle (jp:assocv 'ent b)) " [" (jp:assocv 'typ b) "]"
            " | dXY=" (rtos (nth 2 x) 2 8)
            " | dZ=" (rtos (nth 3 x) 2 8))))))
  (princ))

(defun c:JPLUSRESET ()
  ;; Util quando o proprio Visual LISP do AutoCAD solicita reset de namespace.
  ;; Nao altera geometria; apenas recarrega COM no namespace corrente.
  (vl-load-com)
  (setq *JP-STOP* nil *JP-SKIP* '())
  (princ "\nJPLUS: estado local reinicializado. Se o AutoCAD exibiu 'type Y to reset', digite Y primeiro e recarregue esta LISP.")
  (princ))

(defun c:JPLUS ()
  (princ "\nJPLUS v2.0 - correcao automatica")
  (jp:main "AUTO"))

(defun c:JPLU ()
  ;; Alias curto para quem ja digita JPLU.
  (c:JPLUS))

(defun c:JPLUSA ()
  (princ "\nJPLUSA v2.0 - diagnostico + correcao assistida")
  (jp:main "ASK"))

(defun c:JPLUSL ()
  (princ "\nJPLUSL v2.0 - ligar trechos separados com novas LINEs")
  (jp:bridge-main))

(princ)


;;; =================================================================
;;; v2.1 - consolidacao final reforcada
;;; =================================================================
;;; A geometria/topologia da v2.0 e mantida. Esta versao reforca apenas
;;; a etapa final de JOIN:
;;;   1) JOIN nativo nos dois sentidos;
;;;   2) PEDIT com uma entidade como FONTE + Join da outra;
;;;   3) inverte a fonte;
;;;   4) FILLET real com raio 0 no par (pick proximo ao endpoint);
;;;   5) PEDIT/JOIN novamente.
;;; Isso evita depender exclusivamente do PEDIT Multiple para pares que ja
;;; estao geometricamente prontos.

(defun jp:pick-near-terminal (d / p q f)
  ;; Retorna um ponto um pouco para dentro do segmento terminal. O FILLET
  ;; precisa saber QUAL segmento da polyline foi escolhido.
  (setq p (jp:assocv 'pt d)
        q (jp:assocv 'inner d)
        f 0.08)
  (if (and (jp:valid-pt-p p) (jp:valid-pt-p q))
    (list (+ (car p) (* f (- (car q) (car p))))
          (+ (cadr p) (* f (- (cadr q) (cadr p))))
          (+ (jp:z p) (* f (- (jp:z q) (jp:z p)))))
    p))

(defun jp:run-pedit-source-two (e1 e2 / oldpea ret)
  ;; Fluxo simples do PEDIT: escolhe uma entidade como fonte e adiciona a
  ;; outra com Join. E mais previsivel para um par pronto que PEDIT Multiple.
  (if (and (jp:alive-p e1) (jp:alive-p e2)
           (jp:pedit-joinable-p e1) (jp:pedit-joinable-p e2))
    (progn
      (setq oldpea (getvar "PEDITACCEPT"))
      (setvar "PEDITACCEPT" 1)
      (setq ret
        (vl-catch-all-apply
          '(lambda ()
             ;; 1o Enter encerra a selecao do Join; 2o Enter encerra PEDIT.
             (command-s "_.PEDIT" e1 "_J" e2 "" ""))))
      (setvar "PEDITACCEPT" oldpea)
      (not (vl-catch-all-error-p ret)))
    nil))

(defun jp:run-fillet-zero-candidate (a b / e1 e2 p1 p2 oldrad oldtrim ret)
  ;; Fallback que reproduz o FILLET manual que funciona nesses encontros.
  ;; Restrito a objetos 2D aceitos pelo FILLET; 3D POLYLINE fica de fora.
  (setq e1 (jp:assocv 'ent a)
        e2 (jp:assocv 'ent b))
  (if (and (jp:alive-p e1) (jp:alive-p e2)
           (not (jp:poly3d-p e1)) (not (jp:poly3d-p e2))
           (member (jp:etype e1) '("LINE" "ARC" "LWPOLYLINE" "POLYLINE"))
           (member (jp:etype e2) '("LINE" "ARC" "LWPOLYLINE" "POLYLINE")))
    (progn
      (setq p1 (jp:pick-near-terminal a)
            p2 (jp:pick-near-terminal b)
            oldrad (getvar "FILLETRAD")
            oldtrim (getvar "TRIMMODE"))
      (setvar "FILLETRAD" 0.0)
      (setvar "TRIMMODE" 1)
      (setq ret
        (vl-catch-all-apply
          '(lambda ()
             ;; A lista (ename pickpoint) faz o mesmo papel do clique manual
             ;; perto da ponta/segmento que deve participar do FILLET.
             (command-s "_.FILLET"
                        (list e1 (trans p1 0 1))
                        (list e2 (trans p2 0 1))))))
      (setvar "FILLETRAD" oldrad)
      (setvar "TRIMMODE" oldtrim)
      (not (vl-catch-all-error-p ret)))
    nil))

(defun jp:join-cycle-state (pool mark / p)
  (setq p (jp:refresh-pool pool mark))
  (list p (length p) (jp:pool-signature p)))

(defun jp:join-one-pair-cycle (pool cand tol / a b e1 e2 before sig0 mark st after sig1)
  ;; v2.1: reforco exclusivamente na consolidacao do par.
  (setq a (nth 0 cand)
        b (nth 1 cand)
        e1 (jp:assocv 'ent a)
        e2 (jp:assocv 'ent b)
        pool (jp:uniq-alive pool)
        before (length pool)
        sig0 (jp:pool-signature pool))

  ;; Endpoints ficam numericamente identicos antes de qualquer tentativa.
  (jp:canonicalize-join-pair a b)

  ;; 1) JOIN A -> B.
  (setq mark (entlast))
  (jp:run-native-join-two e1 e2)
  (setq st (jp:join-cycle-state pool mark)
        pool (car st) after (cadr st) sig1 (caddr st))

  ;; 2) JOIN B -> A.
  (if (and (= after before) (equal sig1 sig0) (jp:alive-p e1) (jp:alive-p e2))
    (progn
      (setq mark (entlast))
      (jp:run-native-join-two e2 e1)
      (setq st (jp:join-cycle-state pool mark)
            pool (car st) after (cadr st) sig1 (caddr st))))

  ;; 3) PEDIT com A como fonte.
  (if (and (= after before) (equal sig1 sig0) (jp:alive-p e1) (jp:alive-p e2))
    (progn
      (setq mark (entlast))
      (jp:run-pedit-source-two e1 e2)
      (setq st (jp:join-cycle-state pool mark)
            pool (car st) after (cadr st) sig1 (caddr st))))

  ;; 4) PEDIT com B como fonte.
  (if (and (= after before) (equal sig1 sig0) (jp:alive-p e1) (jp:alive-p e2))
    (progn
      (setq mark (entlast))
      (jp:run-pedit-source-two e2 e1)
      (setq st (jp:join-cycle-state pool mark)
            pool (car st) after (cadr st) sig1 (caddr st))))

  ;; 5) Se ainda recusou, executa FILLET R=0 REAL no par, escolhendo o
  ;; segmento perto do no, exatamente como no uso manual.
  (if (and (= after before) (equal sig1 sig0) (jp:alive-p e1) (jp:alive-p e2))
    (progn
      (setq mark (entlast))
      (jp:run-fillet-zero-candidate a b)
      (setq st (jp:join-cycle-state pool mark)
            pool (car st) after (cadr st) sig1 (caddr st))))

  ;; FILLET pode apenas aparar/estender e manter duas entidades. Nesse caso
  ;; tenta PEDIT-fonte imediatamente, ja com a geometria exatamente pronta.
  (if (and (= after before) (jp:alive-p e1) (jp:alive-p e2))
    (progn
      ;; Regrava novamente o encontro, pois FILLET pode ter reconstruido
      ;; alguma extremidade internamente.
      (setq a (jp:get-terminal-data e1 (jp:assocv 'side a))
            b (jp:get-terminal-data e2 (jp:assocv 'side b)))
      (if (and a b)
        (jp:canonicalize-join-pair a b))
      (setq mark (entlast))
      (jp:run-pedit-source-two e1 e2)
      (setq st (jp:join-cycle-state pool mark)
            pool (car st) after (cadr st) sig1 (caddr st))))

  ;; Ultima tentativa nativa caso o PEDIT apenas tenha convertido o tipo.
  (if (and (= after before) (jp:alive-p e1) (jp:alive-p e2))
    (progn
      (setq mark (entlast))
      (jp:run-native-join-two e1 e2)
      (setq st (jp:join-cycle-state pool mark)
            pool (car st) after (cadr st) sig1 (caddr st))))

  (cond
    ((< after before)
     (list (cons 'status "JOINED")
           (cons 'reduced (- before after))
           (cons 'pool pool)))
    ((not (equal sig1 sig0))
     (list (cons 'status "CHANGED")
           (cons 'reduced 0)
           (cons 'pool pool)))
    (T
     (list (cons 'status "FAILED")
           (cons 'reduced 0)
           (cons 'pool pool)))))

;; Apenas atualiza os wrappers/versionamento. Todo o motor de poda continua
;; sendo o mesmo da v2.0.
(defun c:JPLUS ()
  (princ "\nJPLUS v2.1 - correcao automatica")
  (jp:main "AUTO"))

(defun c:JPLU () (c:JPLUS))

(defun c:JPLUSA ()
  (princ "\nJPLUSA v2.1 - diagnostico + correcao assistida")
  (jp:main "ASK"))

(defun c:JPLUSL ()
  (princ "\nJPLUSL v2.1 - ligar trechos separados com novas LINEs")
  (jp:bridge-main))

(princ)

;;; =================================================================
;;; v2.2 - intersecoes XY defensivas (nil-safe)
;;; =================================================================
;;; Um subsegmento que o Civil 3D nao consegue parametrizar nao pode mais
;;; abortar toda a selecao com "bad argument type: numberp: nil".

(setq *JP-BAD-INTERNAL-SEGS* 0)

(defun jp:make-seg-data (e a b / p0 p1 pm kind d0 d1 len cc r a0 am a1 ccw)
  (if (and (jp:alive-p e) (numberp a) (numberp b) (> (- b a) *JP-EPS*))
    (progn
      (setq p0 (jp:safe-call 'vlax-curve-getPointAtParam (list e a))
            p1 (jp:safe-call 'vlax-curve-getPointAtParam (list e b))
            pm (jp:safe-call 'vlax-curve-getPointAtParam (list e (/ (+ a b) 2.0)))
            d0 (jp:safe-call 'vlax-curve-getDistAtParam (list e a))
            d1 (jp:safe-call 'vlax-curve-getDistAtParam (list e b)))
      (if (and (jp:valid-pt-p p0) (jp:valid-pt-p p1) (jp:valid-pt-p pm))
        (progn
          (setq kind (jp:seg-kind p0 pm p1)
                len (if (and (numberp d0) (numberp d1))
                      (jp:abs (- d1 d0))
                      (distance p0 p1)))
          (if (= kind "ARC")
            (progn
              (setq cc (jp:circumcenter2d p0 pm p1))
              (if cc
                (setq r   (jp:dist2d cc p0)
                      a0  (angle cc p0)
                      am  (angle cc pm)
                      a1  (angle cc p1)
                      ccw (jp:between-ccw-p a0 am a1))
                (setq kind "LINE"))))
          (list (cons 'ent e)
                (cons 'a a) (cons 'b b)
                (cons 'p0 p0) (cons 'p1 p1) (cons 'pm pm)
                (cons 'kind kind) (cons 'len len)
                (cons 'cc cc) (cons 'r r)
                (cons 'a0 a0) (cons 'a1 a1) (cons 'ccw ccw)))
        (progn
          (setq *JP-BAD-INTERNAL-SEGS* (1+ *JP-BAD-INTERNAL-SEGS*))
          nil)))
    nil))

(defun jp:curve-segments (e / typ sp ep out i a b s)
  (setq out '())
  (if (and (jp:alive-p e) (jp:curve-p e))
    (progn
      (setq typ (jp:etype e)
            sp (jp:safe-call 'vlax-curve-getStartParam (list e))
            ep (jp:safe-call 'vlax-curve-getEndParam (list e)))
      (if (and (numberp sp) (numberp ep) (> (- ep sp) *JP-EPS*))
        (cond
          ((member typ '("LINE" "ARC"))
           (setq s (jp:make-seg-data e sp ep))
           (if s (setq out (list s))))
          (T
           (setq i (fix sp))
           (while (< (* 1.0 i) (- ep *JP-EPS*))
             (setq a (max sp (* 1.0 i))
                   b (min ep (+ (* 1.0 i) 1.0)))
             (if (> (- b a) *JP-EPS*)
               (progn
                 (setq s (jp:make-seg-data e a b))
                 (if s (setq out (cons s out)))))
             (setq i (1+ i)))
           (setq out (reverse out))))
        (setq *JP-BAD-INTERNAL-SEGS* (1+ *JP-BAD-INTERNAL-SEGS*)))))
  out)

(defun jp:all-segments (ents / out e segs s)
  (setq out '())
  (foreach e ents
    (if (jp:alive-p e)
      (progn
        (setq segs (jp:safe-call 'jp:curve-segments (list e)))
        (if (listp segs)
          (foreach s segs
            (if s (setq out (cons s out))))))))
  (reverse out))

(defun jp:curve-length (e / ep val)
  (if (and (jp:alive-p e) (jp:curve-p e))
    (progn
      (setq ep (jp:safe-call 'vlax-curve-getEndParam (list e)))
      (if (numberp ep)
        (progn
          (setq val (jp:safe-call 'vlax-curve-getDistAtParam (list e ep)))
          (if (numberp val) val 0.0))
        0.0))
    0.0))

(defun jp:line-frac-at-pt (s p / p0 p1 dx dy den frac)
  (setq p0 (jp:assocv 'p0 s)
        p1 (jp:assocv 'p1 s))
  (if (and (jp:valid-pt-p p0) (jp:valid-pt-p p1) (jp:valid-pt-p p))
    (progn
      (setq dx (- (car p1) (car p0))
            dy (- (cadr p1) (cadr p0))
            den (+ (* dx dx) (* dy dy)))
      (if (> den 1.0e-16)
        (setq frac (/ (+ (* (- (car p) (car p0)) dx)
                           (* (- (cadr p) (cadr p0)) dy)) den))
        (setq frac 0.0))
      (jp:clamp01 frac))
    0.0))

(defun jp:arc-frac-at-pt (s p / cc a0 a1 ap total part)
  (setq cc (jp:assocv 'cc s)
        a0 (jp:assocv 'a0 s)
        a1 (jp:assocv 'a1 s))
  (if (and (jp:valid-pt-p cc) (jp:valid-pt-p p)
           (numberp a0) (numberp a1))
    (progn
      (setq ap (angle cc p))
      (if (jp:assocv 'ccw s)
        (setq total (jp:ccw-delta a0 a1)
              part  (jp:ccw-delta a0 ap))
        (setq total (jp:ccw-delta a1 a0)
              part  (jp:ccw-delta ap a0)))
      (if (and (numberp total) (numberp part) (> total 1.0e-12))
        (jp:clamp01 (/ part total))
        0.0))
    (jp:line-frac-at-pt s p)))

(defun jp:seg-param-at-xy (s p / f a b)
  (setq a (jp:assocv 'a s)
        b (jp:assocv 'b s))
  (if (and (numberp a) (numberp b))
    (progn
      (setq f (if (= (jp:assocv 'kind s) "ARC")
                (jp:arc-frac-at-pt s p)
                (jp:line-frac-at-pt s p)))
      (if (numberp f)
        (+ a (* f (- b a)))
        nil))
    nil))

(defun jp:seg-point3d-at-xy (s p / e par q p0 p1 f)
  (setq e (jp:assocv 'ent s)
        par (jp:seg-param-at-xy s p)
        q nil)
  (if (and (jp:alive-p e) (numberp par))
    (setq q (jp:safe-call 'vlax-curve-getPointAtParam (list e par))))
  (if (jp:valid-pt-p q)
    q
    (progn
      (setq p0 (jp:assocv 'p0 s)
            p1 (jp:assocv 'p1 s)
            f (jp:line-frac-at-pt s p))
      (if (and (jp:valid-pt-p p) (jp:valid-pt-p p0) (jp:valid-pt-p p1)
               (numberp f))
        (list (car p) (cadr p)
              (+ (jp:z p0) (* f (- (jp:z p1) (jp:z p0)))))
        nil))))

(defun jp:entity-interior-xy-p (e p eps / s en)
  (if (and (jp:alive-p e) (jp:valid-pt-p p) (numberp eps))
    (if (jp:closed-p e)
      T
      (progn
        (setq s (jp:safe-call 'vlax-curve-getStartPoint (list e))
              en (jp:safe-call 'vlax-curve-getEndPoint (list e)))
        (and (jp:valid-pt-p s) (jp:valid-pt-p en)
             (> (jp:dist2d p s) eps)
             (> (jp:dist2d p en) eps))))
    nil))

(defun jp:tip-cost-at-xy (seg p / e par total val d)
  (setq e (jp:assocv 'ent seg)
        par (jp:seg-param-at-xy seg p)
        total (jp:curve-length e))
  (if (and (jp:alive-p e) (numberp par) (numberp total))
    (progn
      (setq val (jp:safe-call 'vlax-curve-getDistAtParam (list e par)))
      (if (numberp val)
        (progn
          (setq d val)
          (min d (max 0.0 (- total d))))
        1.0e99))
    1.0e99))

(defun jp:xy-eval-point (s1 s2 p eps / e1 e2 q1 q2 z1 z2 dz key score)
  (setq e1 (jp:assocv 'ent s1)
        e2 (jp:assocv 'ent s2))
  (if (and (jp:alive-p e1) (jp:alive-p e2) (jp:valid-pt-p p))
    (progn
      (setq q1 (jp:seg-point3d-at-xy s1 p)
            q2 (jp:seg-point3d-at-xy s2 p))
      (if (and (jp:valid-pt-p q1) (jp:valid-pt-p q2))
        (progn
          (setq z1 (jp:z q1)
                z2 (jp:z q2)
                dz (jp:abs (- z1 z2))
                key (jp:xycross-key e1 e2 p))
          (if (and (not (jp:skipped-p key))
                   (or (jp:entity-interior-xy-p e1 p eps)
                       (jp:entity-interior-xy-p e2 p eps)
                       (> dz eps)))
            (progn
              (setq score (+ (jp:tip-cost-at-xy s1 p)
                             (jp:tip-cost-at-xy s2 p)))
              (if (numberp score)
                (list score (list s1 s2 p q1 q2 dz key))
                nil))
            nil))
        nil))
    nil))

(defun jp:xy-best-for-pair (s1 s2 eps / pts p r best bestscore)
  (setq pts (jp:safe-call 'jp:seg-ints2d (list s1 s2))
        best nil
        bestscore 1.0e99)
  (if (listp pts)
    (foreach p pts
      (setq r (jp:xy-eval-point s1 s2 p eps))
      (if (and r (numberp (car r)) (< (car r) bestscore))
        (setq bestscore (car r)
              best r))))
  best)

(defun jp:find-xy-cross (ents tol / segs i j s1 s2 e1 e2 r best bestscore eps)
  (setq segs (jp:all-segments ents)
        i 0
        best nil
        bestscore 1.0e99
        eps (jp:node-eps tol))
  (while (< i (length segs))
    (setq s1 (nth i segs)
          e1 (jp:assocv 'ent s1)
          j (1+ i))
    (while (< j (length segs))
      (setq s2 (nth j segs)
            e2 (jp:assocv 'ent s2))
      (if (and s1 s2 (/= e1 e2) (jp:alive-p e1) (jp:alive-p e2))
        (progn
          (setq r (jp:xy-best-for-pair s1 s2 eps))
          (if (and r (numberp (car r)) (< (car r) bestscore))
            (setq bestscore (car r)
                  best (cadr r)))))
      (setq j (1+ j)))
    (setq i (1+ i)))
  best)

;; Wrappers/versionamento v2.2.
(defun c:JPLUS ()
  (setq *JP-BAD-INTERNAL-SEGS* 0)
  (princ "\nJPLUS v2.2 - correcao automatica")
  (jp:main "AUTO")
  (if (> *JP-BAD-INTERNAL-SEGS* 0)
    (princ (strcat "\n[JPLUS] Aviso: " (itoa *JP-BAD-INTERNAL-SEGS*)
                   " segmento(s) interno(s) sem parametrizacao valida foram ignorados individualmente.")))
  (princ))

(defun c:JPLU () (c:JPLUS))

(defun c:JPLUSA ()
  (setq *JP-BAD-INTERNAL-SEGS* 0)
  (princ "\nJPLUSA v2.2 - diagnostico + correcao assistida")
  (jp:main "ASK")
  (if (> *JP-BAD-INTERNAL-SEGS* 0)
    (princ (strcat "\n[JPLUS] Aviso: " (itoa *JP-BAD-INTERNAL-SEGS*)
                   " segmento(s) interno(s) sem parametrizacao valida foram ignorados individualmente.")))
  (princ))

(defun c:JPLUSL ()
  (princ "\nJPLUSL v2.2 - ligar trechos separados com novas LINEs")
  (jp:bridge-main))

(princ)

;;; =================================================================
;;; v2.3 - hierarquia CONSERVADORA para selecao multipla
;;; =================================================================
;;; Regra principal:
;;;   1) Primeiro ENCOSTA endpoints dentro da tolerancia e faz JOIN.
;;;      Nenhuma poda e feita nessa etapa.
;;;   2) FILLET R=0 terminal so move endpoints para a intersecao; nao corta
;;;      trechos internos.
;;;   3) Podas de intersecoes internas / endpoint->interior / overlaps ficam
;;;      restritas a:
;;;         - selecao de exatamente 2 entidades; ou
;;;         - JPLUSA (assistido, com confirmacao); ou
;;;         - JPLUSX (agressivo automatico, comportamento anterior).
;;;   4) OVERKILL automatico tambem nao roda em selecao multipla no JPLUS.
;;;
;;; Objetivo: em lotes grandes, nunca "sair cortando" geometrias que apenas
;;; cruzam ou passam perto umas das outras. O caso dominante e mover a ponta
;;; menor/reta para a ponta de referencia e depois JOIN.

(defun jp:fix-endpair-move-once (ents tol zmode mode / iss a b pa pb mv fx z targetFx targetMv title p key fx2)
  ;; Versao conservadora do endpoint-endpoint: NUNCA usa intersecao das
  ;; direcoes e NUNCA poda. Apenas escolhe quem deve se mover pela hierarquia
  ;; ARC > LINE / segmento maior > menor e faz os endpoints coincidirem.
  (setq iss (jp:find-endpair ents tol))
  (if iss
    (progn
      (setq a (nth 0 iss)
            b (nth 1 iss)
            pa (jp:assocv 'pt a)
            pb (jp:assocv 'pt b)
            mv (jp:choose-mover a b)
            fx (if (= mv a) b a)
            z  (jp:desired-z (jp:z pa) (jp:z pb) zmode)
            p  (jp:ptz (jp:assocv 'pt fx) z)
            key (jp:pair-key "END" a b)
            title
              (cond
                ((<= (nth 2 iss) *JP-EPS*)
                 (strcat "ENCOSTAR PONTAS / COTA | dz=" (rtos (nth 3 iss) 2 4)))
                (T
                 (strcat "ENCOSTAR PONTAS + JOIN | dXY=" (rtos (nth 2 iss) 2 4)))))
      (if (jp:confirm-p mode title a b p tol)
        (progn
          ;; A referencia preserva XY. Se a regra de cota exigir outro Z,
          ;; atualiza apenas sua elevacao no endpoint envolvido.
          (setq targetFx (jp:ptz (jp:assocv 'pt fx) z))
          (jp:set-endpoint fx targetFx)

          ;; Rele a referencia depois da alteracao para usar EXATAMENTE o
          ;; mesmo ponto armazenado pelo AutoCAD, sem diferencas numericas.
          (setq fx2 (jp:get-terminal-data (jp:assocv 'ent fx) (jp:assocv 'side fx)))
          (if fx2
            (setq targetMv (jp:assocv 'pt fx2))
            (setq targetMv targetFx))
          (jp:set-endpoint mv targetMv)
          (list T title))
        (progn
          (if (not *JP-STOP*) (jp:add-skip key))
          (list 'SKIP title))))
    nil))

(defun jp:main-safe (mode / *error* oldcmdecho oldpea ss rawents ents ignored tol zmode pass res
                              cEnd cInt cOv cCross cXy consolidated overkilled phase undo-open
                              closedcnt invalidcnt e nsel destructive-allowed skipped-destructive)
  (vl-load-com)
  (setq oldcmdecho (getvar "CMDECHO")
        oldpea (getvar "PEDITACCEPT")
        *JP-STOP* nil
        *JP-SKIP* '()
        phase "inicializacao"
        undo-open nil)

  (defun *error* (msg)
    (redraw)
    (setvar "CMDECHO" oldcmdecho)
    (setvar "PEDITACCEPT" oldpea)
    (if undo-open
      (progn
        (command-s "_.UNDO" "_End")
        (setq undo-open nil)))
    (if (and msg (not (wcmatch (strcase msg) "*CANCEL*,*QUIT*")))
      (princ (strcat "\n[JPLUS] Erro na etapa [" phase "]: " msg)))
    (princ))

  (setq ss (jp:get-selection))
  (if (null ss)
    (princ "\n[JPLUS] Nenhum objeto valido selecionado.")
    (progn
      (setq rawents (jp:ss->list ss)
            ents (vl-remove-if-not 'jp:usable-open-p rawents)
            ignored (- (length rawents) (length ents))
            nsel (length ents)
            tol (jp:get-tol)
            zmode (jp:get-zmode)
            cEnd 0 cInt 0 cOv 0 cCross 0 cXy 0
            consolidated nil overkilled nil
            closedcnt 0 invalidcnt 0
            ;; No automatico, poda interna somente quando a intencao e
            ;; inequivoca: exatamente DUAS entidades.
            destructive-allowed (or (= mode "ASK") (= nsel 2))
            skipped-destructive nil)

      (foreach e rawents
        (if (not (member e ents))
          (if (jp:closed-p e)
            (setq closedcnt (1+ closedcnt))
            (setq invalidcnt (1+ invalidcnt)))))

      (princ (strcat "\n[JPLUS] Objetos selecionados: " (itoa (length rawents))))
      (if (> ignored 0)
        (princ
          (strcat " | ignorados: " (itoa ignored)
                  " (fechados: " (itoa closedcnt)
                  " / sem extremidade valida: " (itoa invalidcnt) ")")))

      (if (< nsel 2)
        (princ "\n[JPLUS] Menos de 2 entidades abertas utilizaveis.")
        (progn
          (setvar "CMDECHO" 0)
          (command-s "_.UNDO" "_Begin")
          (setq undo-open T)

          ;; 1) CASO PRINCIPAL: endpoints proximos -> mover apenas o endpoint
          ;; escolhido pela hierarquia. Sem FILLET, sem poda, sem BREAK.
          (setq phase "encostar-endpoints" pass 0)
          (while (and (< pass *JP-MAXPASS*) (not *JP-STOP*)
                      (setq res (jp:fix-endpair-move-once ents tol zmode mode)))
            (if (eq (car res) T) (setq cEnd (1+ cEnd)))
            (setq pass (1+ pass)))

          ;; 2) FILLET R=0 nao destrutivo: somente quando as DUAS pontas
          ;; terminais ultrapassaram pouco o cruzamento. Apenas move endpoints.
          (setq phase "fillet-r0-pontas-nao-destrutivo" pass 0)
          (while (and (< pass *JP-MAXPASS*) (not *JP-STOP*)
                      (setq res (jp:fix-terminal-cross-once ents tol zmode mode)))
            (if (eq (car res) T) (setq cCross (1+ cCross)))
            (setq pass (1+ pass)))

          ;; 3) Repassa endpoint-endpoint porque o FILLET terminal pode ter
          ;; criado novas coincidencias simples.
          (setq phase "revisao-endpoints" pass 0)
          (while (and (< pass *JP-MAXPASS*) (not *JP-STOP*)
                      (setq res (jp:fix-endpair-move-once ents tol zmode mode)))
            (if (eq (car res) T) (setq cEnd (1+ cEnd)))
            (setq pass (1+ pass)))

          ;; 4) Operacoes DESTRUTIVAS somente em par isolado ou assistido.
          (if destructive-allowed
            (progn
              ;; Intersecao no meio: poda por MENOS VERTICES, comprimento
              ;; apenas como desempate.
              (setq phase "poda-intersecoes-xy" pass 0)
              (while (and (< pass *JP-MAXPASS*) (not *JP-STOP*)
                          (setq res (jp:fix-xy-cross-once ents tol zmode mode)))
                (if (eq (car res) T) (setq cXy (1+ cXy)))
                (if (and (listp res) (nth 2 res))
                  (setq ents (nth 2 res)))
                (setq pass (1+ pass)))

              ;; Endpoint chegando no interior: so aqui o receptor pode ser
              ;; podado para deixar de ser T e formar uma cadeia JOINavel.
              (setq phase "endpoint-interior-com-poda" pass 0)
              (while (and (< pass *JP-MAXPASS*) (not *JP-STOP*)
                          (setq res (jp:fix-end-to-curve-once ents tol zmode mode)))
                (if (eq (car res) T) (setq cInt (1+ cInt)))
                (if (and (listp res) (nth 2 res))
                  (setq ents (nth 2 res)))
                (setq pass (1+ pass)))

              ;; Overlap explicito tambem pode cortar; por isso nao roda
              ;; indiscriminadamente no automatico com muitos objetos.
              (setq phase "overlap-controlado" pass 0)
              (while (and (< pass *JP-MAXPASS*) (not *JP-STOP*)
                          (setq res (jp:fix-line-overlap-once ents tol zmode mode)))
                (if (eq (car res) T) (setq cOv (1+ cOv)))
                (setq pass (1+ pass)))

              ;; OVERKILL so entra no mesmo regime controlado.
              (setq phase "overkill-controlado"
                    ents (vl-remove-if-not 'jp:alive-p ents))
              (if (and (not *JP-STOP*) (> (length ents) 1))
                (setq overkilled (jp:run-overkill ents tol mode))))
            (setq skipped-destructive T))

          ;; 5) Ultimo repasse conservador antes do JOIN.
          (setq phase "ultima-revisao-endpoints" pass 0
                ents (vl-remove-if-not 'jp:alive-p ents))
          (while (and (< pass *JP-MAXPASS*) (not *JP-STOP*)
                      (setq res (jp:fix-endpair-move-once ents tol zmode mode)))
            (if (eq (car res) T) (setq cEnd (1+ cEnd)))
            (setq pass (1+ pass)))

          ;; 6) JOIN par-a-par reforcado. Nao modifica pares que nao tenham
          ;; endpoints compativeis dentro da tolerancia.
          (setq phase "join-final"
                ents (vl-remove-if-not 'jp:alive-p ents))
          (if (and (not *JP-STOP*) (> (length ents) 1))
            (setq consolidated (jp:run-pedit-join ents tol)))

          (if undo-open
            (progn
              (command-s "_.UNDO" "_End")
              (setq undo-open nil)))
          (setvar "CMDECHO" oldcmdecho)
          (setvar "PEDITACCEPT" oldpea)
          (redraw)

          (princ
            (strcat
              "\n[JPLUS] Concluido - modo "
              (if (= mode "AUTO") "SEGURO" "ASSISTIDO") "."
              "\n  Objetos de entrada: " (itoa (sslength ss))
              (if (> ignored 0)
                (strcat "\n  Ignorados: " (itoa ignored)
                        " (fechados: " (itoa closedcnt)
                        " / sem extremidade valida: " (itoa invalidcnt) ")")
                "")
              "\n  Pontas apenas encostadas: " (itoa cEnd)
              "\n  FILLET R=0 terminal (sem poda interna): " (itoa cCross)
              "\n  Intersecoes internas podadas: " (itoa cXy)
              "\n  Endpoint-interior com poda: " (itoa cInt)
              "\n  Overlaps podados: " (itoa cOv)
              (if skipped-destructive
                "\n  Podas internas/OVERKILL: NAO executados nesta selecao multipla."
                "")
              (if overkilled "\n  Limpeza complementar executada." "")
              "\n  JOIN par-a-par final executado."
              "\n  UNDO: uma unica operacao desfaz toda a rotina."))))))
  (princ))

;;; JPLUS = seguro. JPLUSA = assistido. JPLUSX = agressivo antigo.
(defun c:JPLUS ()
  (setq *JP-BAD-INTERNAL-SEGS* 0)
  (princ "\nJPLUS v2.3 - correcao automatica SEGURA")
  (jp:main-safe "AUTO")
  (if (> *JP-BAD-INTERNAL-SEGS* 0)
    (princ (strcat "\n[JPLUS] Aviso: " (itoa *JP-BAD-INTERNAL-SEGS*)
                   " segmento(s) interno(s) sem parametrizacao valida foram ignorados.")))
  (princ))

(defun c:JPLU () (c:JPLUS))

(defun c:JPLUSA ()
  (setq *JP-BAD-INTERNAL-SEGS* 0)
  (princ "\nJPLUSA v2.3 - diagnostico + correcao assistida")
  (jp:main-safe "ASK")
  (if (> *JP-BAD-INTERNAL-SEGS* 0)
    (princ (strcat "\n[JPLUS] Aviso: " (itoa *JP-BAD-INTERNAL-SEGS*)
                   " segmento(s) interno(s) sem parametrizacao valida foram ignorados.")))
  (princ))

(defun c:JPLUSX ()
  ;; Modo legado/agressivo para quando o usuario QUER que a rotina analise e
  ;; pode intersecoes internas em uma selecao grande.
  (setq *JP-BAD-INTERNAL-SEGS* 0)
  (princ "\nJPLUSX v2.3 - correcao AGRESSIVA (poda intersecoes/overlaps)")
  (jp:main "AUTO")
  (if (> *JP-BAD-INTERNAL-SEGS* 0)
    (princ (strcat "\n[JPLUS] Aviso: " (itoa *JP-BAD-INTERNAL-SEGS*)
                   " segmento(s) interno(s) sem parametrizacao valida foram ignorados.")))
  (princ))

(defun c:JPLUSL ()
  (princ "\nJPLUSL v2.3 - ligar trechos separados com novas LINEs")
  (jp:bridge-main))

(princ)


;;; =================================================================
;;; JPLUS v2.4.2 - FAST PATH / CORRECAO CONSERVADORA
;;; =================================================================
;;; OBJETIVO
;;;   Tornar o caso comum (endpoint perto de endpoint) praticamente tao
;;;   rapido quanto o JOIN nativo: corrige somente a ponta necessaria e
;;;   tenta JOIN IMEDIATAMENTE, sem percorrer todos os vertices da polyline.
;;;
;;; ESTRATEGIA / RACIOCINIO
;;;   1) FAST PATH: endpoint -> endpoint, mover pela hierarquia e JOIN nativo.
;;;   2) Se o JOIN nativo falhar, tenta apenas o fallback de JOIN/PEDIT no par.
;;;   3) FILLET R=0 terminal vem depois, ainda sem poda interna.
;;;   4) Analise pesada de intersecoes internas/poda/overlap so acontece se
;;;      tudo acima falhar e a intencao estiver clara (2 objetos ou assistido).
;;;   5) Em JPLUS seguro com selecao multipla, nao ha varredura de todos os
;;;      segmentos internos nem OVERKILL automatico.
;;;   6) Toda vez que uma correcao cria uma ponta joinavel, o JOIN e tentado
;;;      naquele momento. Nao se espera o final da rotina.
;;;
;;; CORRECAO v2.4.2
;;;   Corrige agrupamento estrutural de IFs da primeira v2.4, que causava
;;;   erro de sintaxe no LOAD apesar de o saldo global de parenteses ser zero.
;;;   Validacao adicional aplicada a IF/SETQ/QUOTE/DEFUN.
;;;
;;; MOTIVO DA OTIMIZACAO
;;;   Nas versoes anteriores, duas polylines grandes podiam ter centenas de
;;;   milhares de pares de segmentos internos testados mesmo quando bastava
;;;   mover um unico endpoint. Isso explicava o usuario cancelar com ESC apos
;;;   alguns segundos e o JOIN manual funcionar: a correcao ja havia ocorrido,
;;;   mas a rotina ainda estava nas analises pesadas antes do JOIN final.
;;; =================================================================

(setq *JP-VERSION* "2.4.2")
(setq *JP-FAST-LAST-JOINED* 0)
(setq *JP-FALLBACK-LAST-JOINED* 0)

(defun jp:fast-native-join-pass (ents tol / pool failed cand a b e1 e2 key before after mark joined tries maxtries)
  ;; JOIN realmente leve: somente endpoints candidatos e JOIN nativo.
  ;; Nao usa PEDIT, FILLET, OVERKILL nem analise de segmentos internos.
  (setq pool (jp:uniq-alive ents)
        failed '()
        joined 0
        tries 0
        maxtries (max 8 (* 4 (length pool))))

  (while (and (< tries maxtries)
              (> (length pool) 1)
              (setq cand (jp:join-candidate pool tol failed)))
    (setq tries (1+ tries)
          a (nth 0 cand)
          b (nth 1 cand)
          e1 (jp:assocv 'ent a)
          e2 (jp:assocv 'ent b)
          key (nth 4 cand)
          before (length pool))

    ;; Normaliza XYZ antes do JOIN, inclusive para pontos que visualmente ja
    ;; parecem coincidentes.
    (jp:canonicalize-join-pair a b)

    ;; JOIN com A como fonte.
    (setq mark (entlast))
    (jp:run-native-join-two e1 e2)
    (setq pool (jp:refresh-pool pool mark)
          after (length pool))

    ;; Alguns tipos so aceitam o JOIN no sentido inverso.
    (if (and (= after before) (jp:alive-p e1) (jp:alive-p e2))
      (progn
        (setq mark (entlast))
        (jp:run-native-join-two e2 e1)
        (setq pool (jp:refresh-pool pool mark)
              after (length pool))))

    (if (< after before)
      (progn
        (setq joined (+ joined (- before after)))
        ;; Handles mudaram; falhas antigas deixam de ser relevantes.
        (setq failed '()))
      (setq failed (cons key failed))))

  (setq *JP-FAST-LAST-JOINED* joined)
  (list (cons 'pool pool)
        (cons 'joined joined)
        (cons 'tries tries)))

(defun jp:fast-endpoint-cycle (ents tol zmode mode / pool corrected joined loops limit changed res jr n)
  ;; Corrige uma ponta e imediatamente tenta JOIN. Isso evita o comportamento
  ;; antigo de "corrigir primeiro e ficar varios segundos analisando antes de
  ;; finalmente chegar ao JOIN".
  (setq pool (jp:uniq-alive ents)
        corrected 0
        joined 0
        loops 0
        limit (max 8 (* 3 (length pool)))
        changed T)

  (while (and changed (< loops limit) (> (length pool) 1) (not *JP-STOP*))
    (setq loops (1+ loops)
          changed nil
          res (jp:fix-endpair-move-once pool tol zmode mode))

    (if res
      (progn
        ;; T = corrigiu. SKIP = no assistido, usuario recusou e o par foi
        ;; marcado; ainda devemos procurar os proximos pares.
        (if (eq (car res) T)
          (setq corrected (1+ corrected)))
        (setq changed T)))

    ;; Mesmo sem correcao nova, podem existir endpoints que JA coincidem.
    (setq jr (jp:fast-native-join-pass pool tol)
          pool (jp:assocv 'pool jr)
          n (jp:assocv 'joined jr))
    (if (> n 0)
      (progn
        (setq joined (+ joined n))
        (setq changed T))))

  (list (cons 'pool pool)
        (cons 'corrected corrected)
        (cons 'joined joined)
        (cons 'loops loops)))

(defun jp:reinforced-endpoint-pass (ents tol / pool failed cand res status reduced joined pass limit)
  ;; Fallback ainda BARATO: trabalha apenas com pares de endpoints. Usa os
  ;; reforcos JOIN/PEDIT ja existentes, mas nao toca em segmentos internos.
  (setq pool (jp:uniq-alive ents)
        failed '()
        joined 0
        pass 0
        limit (max 4 (* 2 (length pool))))

  (while (and (< pass limit)
              (> (length pool) 1)
              (setq cand (jp:join-candidate pool tol failed)))
    (setq pass (1+ pass)
          res (jp:join-one-pair-cycle pool cand tol)
          status (jp:assocv 'status res)
          reduced (jp:assocv 'reduced res)
          pool (jp:assocv 'pool res))
    (cond
      ((= status "JOINED")
       (setq joined (+ joined reduced)
             failed '()))
      ((= status "CHANGED")
       ;; Conversao feita; permite nova tentativa com os handles atualizados.
       (setq failed '()))
      (T
       (setq failed (cons (nth 4 cand) failed)))))

  (setq *JP-FALLBACK-LAST-JOINED* joined)
  (list (cons 'pool pool)
        (cons 'joined joined)
        (cons 'passes pass)))

(defun jp:main-safe (mode / *error* oldcmdecho oldpea ss rawents ents ignored tol zmode
                              cEnd cInt cOv cCross cXy cJoinFast cJoinFallback
                              phase undo-open closedcnt invalidcnt e nsel
                              destructive-allowed skipped-destructive fastres fallres
                              res pass heavy-limit overkilled finished)
  (vl-load-com)
  (setq oldcmdecho (getvar "CMDECHO")
        oldpea (getvar "PEDITACCEPT")
        *JP-STOP* nil
        *JP-SKIP* '()
        phase "inicializacao"
        undo-open nil)

  (defun *error* (msg)
    (redraw)
    (setvar "CMDECHO" oldcmdecho)
    (setvar "PEDITACCEPT" oldpea)
    (if undo-open
      (progn
        (command-s "_.UNDO" "_End")
        (setq undo-open nil)))
    (if (and msg (not (wcmatch (strcase msg) "*CANCEL*,*QUIT*")))
      (princ (strcat "\n[JPLUS] Erro na etapa [" phase "]: " msg)))
    (princ))

  (setq ss (jp:get-selection))
  (if (null ss)
    (princ "\n[JPLUS] Nenhum objeto valido selecionado.")
    (progn
      (setq rawents (jp:ss->list ss)
            ents (vl-remove-if-not 'jp:usable-open-p rawents)
            ignored (- (length rawents) (length ents))
            nsel (length ents)
            tol (jp:get-tol)
            zmode (jp:get-zmode)
            cEnd 0 cInt 0 cOv 0 cCross 0 cXy 0
            cJoinFast 0 cJoinFallback 0
            closedcnt 0 invalidcnt 0
            overkilled nil
            skipped-destructive nil
            finished nil)

      (foreach e rawents
        (if (not (member e ents))
          (if (jp:closed-p e)
            (setq closedcnt (1+ closedcnt))
            (setq invalidcnt (1+ invalidcnt)))))

      (princ (strcat "\n[JPLUS] Objetos selecionados: " (itoa (length rawents))))
      (if (> ignored 0)
        (princ
          (strcat " | ignorados: " (itoa ignored)
                  " (fechados: " (itoa closedcnt)
                  " / sem extremidade valida: " (itoa invalidcnt) ")")))

      (if (< nsel 2)
        (princ "\n[JPLUS] Menos de 2 entidades abertas utilizaveis.")
        (progn
          (setvar "CMDECHO" 0)
          (command-s "_.UNDO" "_Begin")
          (setq undo-open T)

          ;; -----------------------------------------------------------
          ;; 1) FAST PATH: o caso mais comum.
          ;; -----------------------------------------------------------
          (setq phase "fast-path-endpoint-join"
                fastres (jp:fast-endpoint-cycle ents tol zmode mode)
                ents (jp:assocv 'pool fastres)
                cEnd (+ cEnd (jp:assocv 'corrected fastres))
                cJoinFast (+ cJoinFast (jp:assocv 'joined fastres)))

          ;; Se tudo virou uma unica cadeia, TERMINA AQUI. Nenhuma analise de
          ;; intersecao/overlap e executada.
          (if (<= (length ents) 1)
            (setq finished T))

          ;; -----------------------------------------------------------
          ;; 2) Fallback de JOIN/PEDIT SOMENTE em endpoints ja proximos.
          ;; Ainda nao percorre segmentos internos.
          ;; -----------------------------------------------------------
          (if (and (not finished) (not *JP-STOP*) (> (length ents) 1))
            (progn
              (setq phase "fallback-join-endpoints"
                    fallres (jp:reinforced-endpoint-pass ents tol)
                    ents (jp:assocv 'pool fallres)
                    cJoinFallback (+ cJoinFallback (jp:assocv 'joined fallres)))
              (if (<= (length ents) 1)
                (setq finished T))))

          ;; -----------------------------------------------------------
          ;; 3) FILLET R=0 TERMINAL, sem BREAK e sem poda interna.
          ;; Depois de CADA correcao, tenta JOIN imediatamente.
          ;; -----------------------------------------------------------
          (if (and (not finished) (not *JP-STOP*) (> (length ents) 1))
            (progn
              (setq phase "fillet-r0-terminal"
                    pass 0
                    heavy-limit (max 4 (* 2 (length ents))))
              (while (and (< pass heavy-limit) (not *JP-STOP*)
                          (setq res (jp:fix-terminal-cross-once ents tol zmode mode)))
                (setq pass (1+ pass))
                (if (eq (car res) T)
                  (progn
                    (setq cCross (1+ cCross))
                    (setq fastres (jp:fast-endpoint-cycle ents tol zmode mode)
                          ents (jp:assocv 'pool fastres)
                          cEnd (+ cEnd (jp:assocv 'corrected fastres))
                          cJoinFast (+ cJoinFast (jp:assocv 'joined fastres)))
                    (if (<= (length ents) 1)
                      (setq finished T)))))))

          ;; Um ultimo fallback por endpoint antes de qualquer busca pesada.
          (if (and (not finished) (not *JP-STOP*) (> (length ents) 1))
            (progn
              (setq phase "fallback-join-pos-fillet"
                    fallres (jp:reinforced-endpoint-pass ents tol)
                    ents (jp:assocv 'pool fallres)
                    cJoinFallback (+ cJoinFallback (jp:assocv 'joined fallres)))
              (if (<= (length ents) 1)
                (setq finished T))))

          ;; -----------------------------------------------------------
          ;; 4) ANALISE PESADA: somente se TODO o fast path falhou.
          ;; No JPLUS automatico, so com exatamente 2 objetos originais.
          ;; No JPLUSA, permitido porque ha confirmacao do usuario.
          ;; -----------------------------------------------------------
          (setq destructive-allowed
            (and (not finished)
                 (or (= mode "ASK") (= nsel 2))))

          (if (and destructive-allowed (not *JP-STOP*) (> (length ents) 1))
            (progn
              ;; Limite propositalmente curto. Uma correcao topologica deve
              ;; ser seguida imediatamente por nova tentativa de JOIN.
              (setq heavy-limit (if (= mode "ASK")
                                  (max 6 (* 2 (length ents)))
                                  4))

              ;; 4A) Intersecao interna / poda por menos vertices.
              (setq phase "poda-intersecao-interna"
                    pass 0)
              (while (and (not finished) (< pass heavy-limit) (not *JP-STOP*)
                          (setq res (jp:fix-xy-cross-once ents tol zmode mode)))
                (setq pass (1+ pass))
                (if (and (listp res) (nth 2 res))
                  (setq ents (nth 2 res)))
                (if (eq (car res) T)
                  (progn
                    (setq cXy (1+ cXy))
                    (setq fastres (jp:fast-endpoint-cycle ents tol zmode mode)
                          ents (jp:assocv 'pool fastres)
                          cEnd (+ cEnd (jp:assocv 'corrected fastres))
                          cJoinFast (+ cJoinFast (jp:assocv 'joined fastres)))
                    (setq fallres (jp:reinforced-endpoint-pass ents tol)
                          ents (jp:assocv 'pool fallres)
                          cJoinFallback (+ cJoinFallback (jp:assocv 'joined fallres)))
                    (if (<= (length ents) 1) (setq finished T)))))

              ;; 4B) Endpoint chegando ao interior.
              (if (and (not finished) (not *JP-STOP*) (> (length ents) 1))
                (progn
                  (setq phase "endpoint-interior"
                        pass 0)
                  (while (and (not finished) (< pass heavy-limit) (not *JP-STOP*)
                              (setq res (jp:fix-end-to-curve-once ents tol zmode mode)))
                    (setq pass (1+ pass))
                    (if (and (listp res) (nth 2 res))
                      (setq ents (nth 2 res)))
                    (if (eq (car res) T)
                      (progn
                        (setq cInt (1+ cInt))
                        (setq fastres (jp:fast-endpoint-cycle ents tol zmode mode)
                              ents (jp:assocv 'pool fastres)
                              cEnd (+ cEnd (jp:assocv 'corrected fastres))
                              cJoinFast (+ cJoinFast (jp:assocv 'joined fastres)))
                        (setq fallres (jp:reinforced-endpoint-pass ents tol)
                              ents (jp:assocv 'pool fallres)
                              cJoinFallback (+ cJoinFallback (jp:assocv 'joined fallres)))
                        (if (<= (length ents) 1) (setq finished T)))))))

              ;; 4C) Overlap. Tambem tenta JOIN imediatamente depois.
              (if (and (not finished) (not *JP-STOP*) (> (length ents) 1))
                (progn
                  (setq phase "overlap-controlado"
                        pass 0)
                  (while (and (not finished) (< pass heavy-limit) (not *JP-STOP*)
                              (setq res (jp:fix-line-overlap-once ents tol zmode mode)))
                    (setq pass (1+ pass))
                    (if (eq (car res) T)
                      (progn
                        (setq cOv (1+ cOv))
                        (setq fastres (jp:fast-endpoint-cycle ents tol zmode mode)
                              ents (jp:assocv 'pool fastres)
                              cEnd (+ cEnd (jp:assocv 'corrected fastres))
                              cJoinFast (+ cJoinFast (jp:assocv 'joined fastres)))
                        (setq fallres (jp:reinforced-endpoint-pass ents tol)
                              ents (jp:assocv 'pool fallres)
                              cJoinFallback (+ cJoinFallback (jp:assocv 'joined fallres)))
                        (if (<= (length ents) 1) (setq finished T)))))))

              ;; OVERKILL NAO roda automaticamente no JPLUS seguro. No
              ;; assistido, mantemos a opcao existente porque ha confirmacao.
              (if (and (= mode "ASK") (not finished) (not *JP-STOP*) (> (length ents) 1))
                (progn
                  (setq phase "overkill-assistido")
                  (setq overkilled (jp:run-overkill ents tol mode)))))
            (if (and (not finished) (> nsel 2))
              (setq skipped-destructive T)))

          ;; -----------------------------------------------------------
          ;; 5) Finalizacao: apenas endpoint/JOIN; nada de nova varredura
          ;; pesada. Isso garante que a rotina nao "fica pensando" depois que
          ;; a geometria ja foi corrigida.
          ;; -----------------------------------------------------------
          (if (and (not *JP-STOP*) (> (length ents) 1))
            (progn
              (setq phase "join-final-leve"
                    fastres (jp:fast-endpoint-cycle ents tol zmode mode)
                    ents (jp:assocv 'pool fastres)
                    cEnd (+ cEnd (jp:assocv 'corrected fastres))
                    cJoinFast (+ cJoinFast (jp:assocv 'joined fastres)))
              (setq fallres (jp:reinforced-endpoint-pass ents tol)
                    ents (jp:assocv 'pool fallres)
                    cJoinFallback (+ cJoinFallback (jp:assocv 'joined fallres)))))

          (if undo-open
            (progn
              (command-s "_.UNDO" "_End")
              (setq undo-open nil)))
          (setvar "CMDECHO" oldcmdecho)
          (setvar "PEDITACCEPT" oldpea)
          (redraw)

          (princ
            (strcat
              "\n[JPLUS] Concluido - v2.4.2 / modo "
              (if (= mode "AUTO") "SEGURO" "ASSISTIDO") "."
              "\n  Objetos de entrada: " (itoa (sslength ss))
              (if (> ignored 0)
                (strcat "\n  Ignorados: " (itoa ignored)
                        " (fechados: " (itoa closedcnt)
                        " / sem extremidade valida: " (itoa invalidcnt) ")")
                "")
              "\n  Endpoints corrigidos: " (itoa cEnd)
              "\n  JOIN nativo no FAST PATH: " (itoa cJoinFast)
              "\n  JOIN/PEDIT de fallback: " (itoa cJoinFallback)
              "\n  FILLET R=0 terminal: " (itoa cCross)
              "\n  Intersecoes internas podadas: " (itoa cXy)
              "\n  Endpoint-interior com poda: " (itoa cInt)
              "\n  Overlaps podados: " (itoa cOv)
              (if skipped-destructive
                "\n  Analise pesada: IGNORADA nesta selecao multipla segura."
                "")
              (if overkilled "\n  OVERKILL assistido executado." "")
              "\n  Entidades abertas restantes: " (itoa (length (jp:uniq-alive ents)))
              "\n  UNDO: uma unica operacao desfaz toda a rotina."))))))
  (princ))

;;; JPLUS = seguro e rapido. JPLUSA = assistido. JPLUSX = agressivo legado.
(defun c:JPLUS ()
  (setq *JP-BAD-INTERNAL-SEGS* 0)
  (princ "\nJPLUS v2.4.2 - correcao automatica SEGURA / FAST PATH")
  (jp:main-safe "AUTO")
  (if (> *JP-BAD-INTERNAL-SEGS* 0)
    (princ (strcat "\n[JPLUS] Aviso: " (itoa *JP-BAD-INTERNAL-SEGS*)
                   " segmento(s) interno(s) sem parametrizacao valida foram ignorados.")))
  (princ))

(defun c:JPLU () (c:JPLUS))

(defun c:JPLUSA ()
  (setq *JP-BAD-INTERNAL-SEGS* 0)
  (princ "\nJPLUSA v2.4.2 - diagnostico + correcao assistida")
  (jp:main-safe "ASK")
  (if (> *JP-BAD-INTERNAL-SEGS* 0)
    (princ (strcat "\n[JPLUS] Aviso: " (itoa *JP-BAD-INTERNAL-SEGS*)
                   " segmento(s) interno(s) sem parametrizacao valida foram ignorados.")))
  (princ))

(defun c:JPLUSX ()
  (setq *JP-BAD-INTERNAL-SEGS* 0)
  (princ "\nJPLUSX v2.4.2 - correcao AGRESSIVA (motor legado)")
  (jp:main "AUTO")
  (if (> *JP-BAD-INTERNAL-SEGS* 0)
    (princ (strcat "\n[JPLUS] Aviso: " (itoa *JP-BAD-INTERNAL-SEGS*)
                   " segmento(s) interno(s) sem parametrizacao valida foram ignorados.")))
  (princ))

(defun c:JPLUSL ()
  (princ "\nJPLUSL v2.4.2 - ligar trechos separados com novas LINEs")
  (jp:bridge-main))



;;; =================================================================
;;; HELP INTEGRADO - comando JPLUSHELP
;;; =================================================================
;;; Este HELP faz parte da propria rotina e nao modifica o desenho.
;;; Mostra comandos, criterios de decisao e o fluxo pratico de correcao.

(defun c:JPLUSHELP ()
  (princ "\n============================================================")
  (princ "\n JPLUS v2.4.2 - AJUDA / PROCEDIMENTO")
  (princ "\n============================================================")

  (princ "\n\nCOMANDOS DISPONIVEIS")
  (princ "\n  JPLUS")
  (princ "\n    Modo principal, automatico, seguro e rapido.")
  (princ "\n    Prioriza mover endpoints proximos e executar JOIN imediatamente.")
  (princ "\n    Em selecao multipla (>2), evita podas internas e analise pesada.")

  (princ "\n\n  JPLU")
  (princ "\n    Alias curto de JPLUS. Faz exatamente a mesma coisa.")

  (princ "\n\n  JPLUSA")
  (princ "\n    Modo assistido/diagnostico.")
  (princ "\n    Mostra os problemas encontrados e permite confirmar correcoes")
  (princ "\n    destrutivas antes de podar/intervir na geometria.")

  (princ "\n\n  JPLUSX")
  (princ "\n    Modo agressivo/legado.")
  (princ "\n    Analisa intersecoes internas, overlaps e outros casos em selecoes")
  (princ "\n    maiores. Use somente quando voce QUER uma limpeza mais pesada.")

  (princ "\n\n  JPLUSL")
  (princ "\n    Liga trechos separados criando novas LINEs entre extremidades livres.")
  (princ "\n    Usa uma distancia maxima informada, evita criar ramificacoes em T")
  (princ "\n    e tenta consolidar a cadeia com JOIN no final.")

  (princ "\n\n  JPLUSFALHAS")
  (princ "\n    Diagnostica os pares da ultima execucao que estavam geometricamente")
  (princ "\n    prontos, mas que o AutoCAD recusou no JOIN/PEDIT.")
  (princ "\n    Exibe handles, tipos, dXY e dZ para investigacao.")

  (princ "\n\n  JPLUSRESET")
  (princ "\n    Reinicializa somente o estado local/Visual LISP da rotina.")
  (princ "\n    Use depois de erros de namespace/estado residual. Nao altera geometria.")

  (princ "\n\n  JPLUSHELP")
  (princ "\n    Exibe esta ajuda. Nao altera o desenho.")

  (princ "\n\n------------------------------------------------------------")
  (princ "\nFLUXO DO JPLUS SEGURO / RAPIDO")
  (princ "\n------------------------------------------------------------")
  (princ "\n  1. Seleciona LINE, ARC, LWPOLYLINE ou POLYLINE abertas.")
  (princ "\n  2. Solicita tolerancia XY e qual cota deve prevalecer")
  (princ "\n     [Superior/Inferior].")
  (princ "\n  3. FAST PATH: procura endpoint perto de endpoint.")
  (princ "\n  4. Move somente a extremidade que deve ceder e grava XYZ exato.")
  (princ "\n  5. Executa JOIN nativo IMEDIATAMENTE.")
  (princ "\n  6. Se falhar, tenta JOIN/PEDIT somente naquele par de endpoints.")
  (princ "\n  7. Se ainda falhar, tenta FILLET R=0 apenas em pontas que")
  (princ "\n     realmente ultrapassaram o encontro e tenta JOIN de novo.")
  (princ "\n  8. Somente se tudo acima falhar, pode entrar em analise pesada.")
  (princ "\n     No JPLUS automatico isso so e permitido com exatamente 2 objetos;")
  (princ "\n     com mais de 2 objetos a poda interna e bloqueada por seguranca.")
  (princ "\n  9. Cada correcao que cria uma ponta joinavel dispara JOIN na hora;")
  (princ "\n     a rotina nao espera o final para tentar consolidar.")

  (princ "\n\n------------------------------------------------------------")
  (princ "\nCRITERIOS DE DECISAO")
  (princ "\n------------------------------------------------------------")
  (princ "\n  - Regra principal: MOVER ENDPOINT + JOIN antes de qualquer poda.")
  (princ "\n  - ARC prevalece sobre LINE quando uma delas precisa ceder.")
  (princ "\n  - Entre elementos equivalentes, o trecho maior tende a prevalecer;")
  (princ "\n    o menor e o candidato preferencial para mover.")
  (princ "\n  - Conflito de Z: prevalece Superior ou Inferior conforme escolhido.")
  (princ "\n  - FILLET R=0/poda e fallback: so deve atuar quando existe")
  (princ "\n    ultrapassagem/interseccao real que nao se resolve apenas encostando.")
  (princ "\n  - Em poda de intersecao interna, descarta primeiro o lado com MENOS")
  (princ "\n    vertices; se empatar, usa o menor comprimento como desempate.")
  (princ "\n  - Selecao multipla no JPLUS e conservadora: nao sai podando cruzamentos.")
  (princ "\n  - Toda a execucao fica dentro de um unico grupo de UNDO.")

  (princ "\n\n------------------------------------------------------------")
  (princ "\nQUAL COMANDO USAR?")
  (princ "\n------------------------------------------------------------")
  (princ "\n  Problema normal de JOIN / pontas quase coincidentes -> JPLUS")
  (princ "\n  Quero ver e aprovar correcoes mais delicadas       -> JPLUSA")
  (princ "\n  Quero limpeza/poda agressiva em varias geometrias  -> JPLUSX")
  (princ "\n  Tenho trechos separados e quero criar ligacoes     -> JPLUSL")
  (princ "\n  JOIN recusou mesmo com pontos coincidentes         -> JPLUSFALHAS")
  (princ "\n  Visual LISP ficou com estado estranho              -> JPLUSRESET")

  (princ "\n\n============================================================")
  (princ "\n Fim da ajuda JPLUS v2.4.2")
  (princ "\n============================================================")
  (princ))

(princ "\nJPLUS v2.4.2 carregado. JPLUS=seguro/rapido / JPLUSA=assistido / JPLUSX=agressivo / JPLUSL=ligar trechos / JPLUSHELP=ajuda.")
(princ)
