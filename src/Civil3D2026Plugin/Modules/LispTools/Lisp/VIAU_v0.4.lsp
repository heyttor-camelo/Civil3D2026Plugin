;;@TITULO  Resolver entroncamentos de vias
;;@CATEGORIA Vias e meio-fio
;;@COMANDO VIAU
;;@COMANDO VIAUCLR
;;@VERSAO  0.4
;;@DESC    Limpa e une a malha de eixos de vias: clicando perto de um entroncamento, calcula a intersecao real,
;;@DESC    quebra no no, une os trechos que continuam e remove duplicatas, seguindo cada ramo ate a proxima
;;@DESC    intersecao provavel. Marca o resultado com cores na layer _REVISAO.
;;@DESC    Versao marcada como NAO TESTADA pelo proprio arquivo: use em copia do desenho.
;;@USO     Digite VIAU e clique perto de cada entroncamento
;;@USO     Siga as marcas amarelas (proximo clique)
;;@USO     VIAUCLR apaga todas as marcas
;; ----------------------------------------------------------------
;;; ------------------------------------------------------------------
;;; VIAU.lsp  v0.4  (NAO TESTADO - testar em COPIA do desenho)
;;; Comandos: VIAU  (resolve entroncamentos por clique)
;;;           VIAUCLR (apaga TODAS as marcas da layer _REVISAO)
;;;
;;; Fluxo: voce clica PERTO de um entroncamento. O clique NAO vira no:
;;; a rotina calcula a interseccao real entre os alinhamentos proximos
;;; (ponto que melhor encosta em todos os ramos) e resolve ali. So se
;;; cria vertice sobre a propria via, para quebrar no entroncamento.
;;; Ramos agrupados por direcao, 1 representante por ramo, duplicatas
;;; removidas -> cada ramo CAMINHA sozinho ao longo da via, unindo as
;;; polilinhas/linhas que continuam (pela geometria real, sem atalho)
;;; e limpando duplicatas, ate achar a proxima interseccao provavel.
;;;
;;; Proxima interseccao provavel =
;;;   - ponta de outra via que chega no alinhamento e se afasta dele;
;;;   - outra via que CRUZA o alinhamento em angulo maior que o limite;
;;;   - na ponta do trecho: 2 ou mais ramos de saida.
;;;   (1 so ramo de saida na ponta = continuacao: une e segue.)
;;;
;;; MARCAS (layer _REVISAO), cada cor tem um significado:
;;;   VERDE    (3) = RESOLVIDO: no que voce clicou e resolveu
;;;   AMARELO  (2) = POSSIVEL ENTRONCAMENTO: a caminhada parou aqui,
;;;                  aguardando seu proximo clique
;;;   VERMELHO (1) = ANALISAR: clique com menos de 2 ramos (nada alterado)
;;;   MAGENTA  (6) = LIMITE/LOOP: caminhada interrompida por seguranca
;;;   CIANO    (4) = FIM DE VIA: a via termina aqui
;;; Ao resolver uma regiao, as marcas AMARELO/VERMELHO/MAGENTA/CIANO que
;;; estiverem sobre o trecho resolvido sao apagadas automaticamente.
;;;
;;; Cada clique e um unico UNDO; erro dentro de um clique desfaz o clique.
;;; Arcos (bulge) sao tratados como retas.
;;; ------------------------------------------------------------------
(vl-load-com)

;; --- utilitarios ----------------------------------------------------
(defun v:2d (p) (list (car p) (cadr p)))

(defun v:pts (e / ed ty r d)
  (setq ed (entget e) ty (cdr (assoc 0 ed)))
  (cond
    ((= ty "LINE")
     (list (v:2d (cdr (assoc 10 ed))) (v:2d (cdr (assoc 11 ed)))))
    ((= ty "LWPOLYLINE")
     (foreach d ed
       (if (= (car d) 10) (setq r (cons (v:2d (cdr d)) r))))
     (setq r (reverse r))
     (if (= 1 (logand 1 (cdr (assoc 70 ed))))
       (setq r (append r (list (car r)))))
     r)))

(defun v:len (pts / s)
  (setq s 0.0)
  (while (cdr pts)
    (setq s (+ s (distance (car pts) (cadr pts))) pts (cdr pts)))
  s)

(defun v:take (n l / r)
  (repeat n
    (if l (setq r (cons (car l) r) l (cdr l))))
  (reverse r))

(defun v:nthcdr (n l)
  (repeat n (setq l (cdr l)))
  l)

(defun v:dedup (pts / r)
  (foreach q pts
    (if (or (null r) (> (distance q (car r)) 1e-6))
      (setq r (cons q r))))
  (reverse r))

(defun v:dseg (p a b / dx dy l2 u)
  (setq dx (- (car b) (car a)) dy (- (cadr b) (cadr a))
        l2 (+ (* dx dx) (* dy dy)))
  (if (< l2 1e-12)
    (distance p a)
    (progn
      (setq u (/ (+ (* (- (car p) (car a)) dx) (* (- (cadr p) (cadr a)) dy)) l2))
      (setq u (max 0.0 (min 1.0 u)))
      (distance p (list (+ (car a) (* u dx)) (+ (cadr a) (* u dy)))))))

(defun v:proj (p a b / dx dy l2 u)
  (setq dx (- (car b) (car a)) dy (- (cadr b) (cadr a))
        l2 (+ (* dx dx) (* dy dy)))
  (if (< l2 1e-12)
    a
    (progn
      (setq u (/ (+ (* (- (car p) (car a)) dx) (* (- (cadr p) (cadr a)) dy)) l2))
      (setq u (max 0.0 (min 1.0 u)))
      (list (+ (car a) (* u dx)) (+ (cadr a) (* u dy))))))

;; retorna (menor-distancia indice-do-segmento-mais-proximo)
(defun v:near (p pts / best i bi d)
  (setq best 1e99 i 0 bi 0)
  (while (cdr pts)
    (setq d (v:dseg p (car pts) (cadr pts)))
    (if (< d best) (setq best d bi i))
    (setq i (1+ i) pts (cdr pts)))
  (list best bi))

(defun v:adiff (a b / d)
  (setq d (abs (- a b)))
  (if (> d pi) (- (* 2 pi) d) d))

(defun v:pdiff (a b / d)
  (setq d (abs (- a b)))
  (while (>= d pi) (setq d (- d pi)))
  (min d (- pi d)))

(defun v:lerp (a b u)
  (list (+ (car a) (* u (- (car b) (car a))))
        (+ (cadr a) (* u (- (cadr b) (cadr a))))))

;; ponto a distancia d (ao longo) a partir do inicio de pts
(defun v:ahead-pt (pts d / acc a b sl)
  (setq acc 0.0)
  (while (and (cdr pts) (< acc d))
    (setq a (car pts) b (cadr pts) sl (distance a b))
    (if (>= (+ acc sl) d)
      (progn
        (setq pts (list (v:lerp a b (/ (- d acc) sl))))
        (setq acc d))
      (setq acc (+ acc sl) pts (cdr pts))))
  (car pts))

;; parametros t (0..1) onde o segmento a-b cruza o circulo c,r
(defun v:cts (a b c r / dx dy fx fy qa qb qc disc sq t1 t2 res)
  (setq dx (- (car b) (car a)) dy (- (cadr b) (cadr a))
        fx (- (car a) (car c)) fy (- (cadr a) (cadr c))
        qa (+ (* dx dx) (* dy dy))
        qb (* 2.0 (+ (* fx dx) (* fy dy)))
        qc (- (+ (* fx fx) (* fy fy)) (* r r)))
  (if (> qa 1e-12)
    (progn
      (setq disc (- (* qb qb) (* 4.0 qa qc)))
      (if (> disc 0.0)
        (progn
          (setq sq (sqrt disc)
                t1 (/ (- (- qb) sq) (* 2.0 qa))
                t2 (/ (+ (- qb) sq) (* 2.0 qa)))
          (if (and (> t1 1e-9) (< t1 (- 1.0 1e-9))) (setq res (cons t1 res)))
          (if (and (> t2 1e-9) (< t2 (- 1.0 1e-9))) (setq res (cons t2 res)))
          (setq res (vl-sort res '<))))))
  res)

;; primeiro ponto onde o ramo br SAI do circulo (c,r)
(defun v:exitpt (br c r / a b ts res)
  (while (and (cdr br) (null res))
    (setq a (car br) b (cadr br) br (cdr br))
    (if (and (<= (distance a c) r) (> (distance b c) r))
      (progn
        (setq ts (v:cts a b c r))
        (setq res (if ts (v:lerp a b (last ts)) b)))))
  res)

;; direcao "de volta" do alinhamento rpts visto da sua ponta ep
(defun v:backbearing (rpts ep / ex)
  (setq ex (v:exitpt (reverse rpts) ep *vR*))
  (if ex (angle ep ex) (angle ep (car rpts))))

;; --- agrupamento de ramos -----------------------------------------------
(defun v:rm2 (l i j / k r)
  (setq k 0)
  (foreach x l
    (if (and (/= k i) (/= k j)) (setq r (cons x r)))
    (setq k (1+ k)))
  (reverse r))

;; stub = (layer pts poly? comprimento angulo ename)
(defun v:close-p (c1 c2 tol)
  (vl-some
    '(lambda (s1)
       (vl-some '(lambda (s2) (< (v:adiff (nth 4 s1) (nth 4 s2)) tol)) c2))
    c1))

(defun v:cluster (stubs tolgrau / cl done i j tol)
  (setq tol (* tolgrau (/ pi 180.0)))
  (setq cl (mapcar 'list stubs) done nil)
  (while (not done)
    (setq done T i 0)
    (while (and done (< i (length cl)))
      (setq j (1+ i))
      (while (and done (< j (length cl)))
        (if (v:close-p (nth i cl) (nth j cl) tol)
          (progn
            (setq cl (cons (append (nth i cl) (nth j cl)) (v:rm2 cl i j)))
            (setq done nil))
          (setq j (1+ j))))
      (setq i (1+ i))))
  cl)

(defun v:backp (c bb tol)
  (vl-some '(lambda (s) (< (v:adiff (nth 4 s) bb) tol)) c))

;; polilinha vence linha; depois o ramo mais longo
(defun v:sortcl (c)
  (vl-sort c
    '(lambda (a b)
       (cond ((and (nth 2 a) (not (nth 2 b))) T)
             ((and (nth 2 b) (not (nth 2 a))) nil)
             (T (> (nth 3 a) (nth 3 b)))))))

;; --- criacao de entidades e marcas ----------------------------------------
(defun v:mkpoly (ly pts)
  (if (and pts (cdr pts))
    (entmakex
      (append
        (list '(0 . "LWPOLYLINE") '(100 . "AcDbEntity") (cons 8 ly)
              '(100 . "AcDbPolyline") (cons 90 (length pts)) '(70 . 0))
        (mapcar '(lambda (q) (cons 10 q)) pts)))))

(defun v:mark (p col)
  (if (not (tblsearch "LAYER" "_REVISAO"))
    (entmake (list '(0 . "LAYER") '(100 . "AcDbSymbolTableRecord")
                   '(100 . "AcDbLayerTableRecord") '(2 . "_REVISAO")
                   '(70 . 0) '(62 . 7) '(6 . "Continuous"))))
  (entmakex (list '(0 . "CIRCLE") '(8 . "_REVISAO") (cons 62 col)
                  (cons 10 p) (cons 40 (* *vR* 0.6)))))

;; apaga marcas cujo centro esta a menos de tol do caminho "path".
;; keepgreen = T preserva as verdes (nos resolvidos)
(defun v:clear-marks (path tol keepgreen / ss i e ed c col)
  (if (setq ss (ssget "_X" '((0 . "CIRCLE") (8 . "_REVISAO"))))
    (progn
      (setq i 0)
      (repeat (sslength ss)
        (setq e (ssname ss i) i (1+ i)
              ed (entget e)
              c (cdr (assoc 10 ed))
              col (cdr (assoc 62 ed)))
        (if (and (<= (car (v:near c path)) tol)
                 (or (not keepgreen) (/= col 3)))
          (entdel e))))))

;; --- selecao e ramos -------------------------------------------------------
(defun v:bbox-ss (pts d / xs ys)
  (setq xs (mapcar 'car pts) ys (mapcar 'cadr pts))
  (ssget "_C"
         (list (- (apply 'min xs) d) (- (apply 'min ys) d))
         (list (+ (apply 'max xs) d) (+ (apply 'max ys) d))
         '((0 . "LINE,LWPOLYLINE"))))

(defun v:mkstub (ly po br n e / ex)
  (if (and (cdr br) (> (distance (last br) n) *vR*))
    (progn
      (setq ex (v:exitpt br n *vR*))
      (if ex (list ly br po (v:len br) (angle n ex) e)))))

;; entidades com ponto mais proximo de n a menos de cap e seus ramos a
;; partir desse ponto, para fora (geometria real, nao so o trecho fora
;; do circulo). Retorna (ents stubs)
(defun v:gather (n cap / ss i e pts ed ly po ents stubs nr idx j br s)
  (setq ss (ssget "_C"
                  (list (- (car n) *vR*) (- (cadr n) *vR*))
                  (list (+ (car n) *vR*) (+ (cadr n) *vR*))
                  '((0 . "LINE,LWPOLYLINE"))))
  (if ss
    (progn
      (setq i 0)
      (repeat (sslength ss)
        (setq e (ssname ss i) i (1+ i) pts (v:pts e))
        (if (and pts (cdr pts))
          (progn
            (setq nr (v:near n pts))
            (if (<= (car nr) cap)
              (progn
                (setq ed (entget e)
                      ly (cdr (assoc 8 ed))
                      po (= (cdr (assoc 0 ed)) "LWPOLYLINE")
                      idx (cadr nr)
                      j (v:proj n (nth idx pts) (nth (1+ idx) pts)))
                (setq ents (cons e ents))
                ;; ramo para frente
                (setq br (v:dedup (cons j (v:nthcdr (1+ idx) pts))))
                (setq s (v:mkstub ly po br n e))
                (if s (setq stubs (cons s stubs)))
                ;; ramo para tras
                (setq br (v:dedup (cons j (reverse (v:take (1+ idx) pts)))))
                (setq s (v:mkstub ly po br n e))
                (if s (setq stubs (cons s stubs))))))))))
  (list ents stubs))

;; --- achar a interseccao real perto do clique ---------------------------
;; pedacos da polilinha que cortam o circulo (c,r) -> partes FORA dele
(defun v:outpieces (pts c r / pcs cur a b sub p0 p1 m)
  (while (cdr pts)
    (setq a (car pts) b (cadr pts) pts (cdr pts)
          sub (append (list a)
                      (mapcar '(lambda (u) (v:lerp a b u)) (v:cts a b c r))
                      (list b)))
    (while (cdr sub)
      (setq p0 (car sub) p1 (cadr sub) sub (cdr sub)
            m (list (/ (+ (car p0) (car p1)) 2.0) (/ (+ (cadr p0) (cadr p1)) 2.0)))
      (if (> (distance m c) r)
        (progn
          (if (null cur) (setq cur (list p0)))
          (if (> (distance p1 (last cur)) 1e-9)
            (setq cur (append cur (list p1)))))
        (if cur (progn (setq pcs (cons cur pcs)) (setq cur nil))))))
  (if cur (setq pcs (cons cur pcs)))
  (reverse pcs))

;; segmentos de pl a menos de rad de p, como lista de (a b)
(defun v:localsegs (pl p rad / r)
  (while (cdr pl)
    (if (<= (v:dseg p (car pl) (cadr pl)) rad)
      (setq r (cons (list (car pl) (cadr pl)) r)))
    (setq pl (cdr pl)))
  r)

;; ponto de pl mais proximo de p
(defun v:nearpt (p pl / nr idx)
  (setq nr (v:near p pl) idx (cadr nr))
  (v:proj p (nth idx pl) (nth (1+ idx) pl)))

;; soma das distancias de c a cada alinhamento
(defun v:sumdist (c plines / s)
  (setq s 0.0)
  (foreach pl plines (setq s (+ s (car (v:near c pl)))))
  s)

;; reps = ramos representantes (stubs). Procura o ponto, SOBRE a
;; geometria existente, que melhor encosta em todos os ramos:
;; candidatos = pontos mais proximos do clique, vertices proximos,
;; projecao de um ramo sobre os outros e cruzamentos entre ramos.
;; retorna (ponto distancia-media-aos-ramos)
(defun v:findnode (p reps / plines prs cands i j a b sa sb ip best bs sc)
  (setq plines (mapcar '(lambda (s) (v:pts (nth 5 s))) reps))
  (setq prs (mapcar '(lambda (pl) (cons pl (v:nearpt p pl))) plines))
  (setq cands nil)
  (foreach pl plines
    (foreach q pl
      (if (<= (distance p q) *vC*) (setq cands (cons q cands)))))
  (setq i 0)
  (foreach a prs
    (setq cands (cons (cdr a) cands))
    (setq j 0)
    (foreach b prs
      (if (/= i j)
        (progn
          (setq cands (cons (v:nearpt (cdr a) (car b)) cands))
          (if (< i j)
            (foreach sa (v:localsegs (car a) p (* 2.0 *vC*))
              (foreach sb (v:localsegs (car b) p (* 2.0 *vC*))
                (setq ip (inters (car sa) (cadr sa) (car sb) (cadr sb) T))
                (if ip (setq cands (cons (list (car ip) (cadr ip)) cands))))))))
      (setq j (1+ j)))
    (setq i (1+ i)))
  (setq best nil bs 1e99)
  (foreach c cands
    (setq sc (+ (v:sumdist c plines) (* 0.02 (distance p c))))
    (if (< sc bs) (setq bs sc best c)))
  (list best (/ (v:sumdist best plines) (length plines))))

;; --- duplicatas -----------------------------------------------------------
;; retorna (n-removidos lista-de-pedacos-mantidos)
(defun v:trimpieces (pts rpts / cur a b m nr keep lim nrem pcs)
  (setq lim (* *vA2* (/ pi 180.0)) nrem 0)
  (while (cdr pts)
    (setq a (car pts) b (cadr pts) pts (cdr pts))
    (if (> (distance a b) 1e-9)
      (progn
        (setq m (list (/ (+ (car a) (car b)) 2.0) (/ (+ (cadr a) (cadr b)) 2.0))
              nr (v:near m rpts)
              keep (not (and (< (car nr) *vT*)
                             (< (car (v:near a rpts)) *vT*)
                             (< (car (v:near b rpts)) *vT*)
                             (< (v:pdiff (angle a b)
                                         (angle (nth (cadr nr) rpts)
                                                (nth (1+ (cadr nr)) rpts)))
                                lim))))
        (if keep
          (if cur
            (setq cur (append cur (list b)))
            (setq cur (list a b)))
          (progn
            (setq nrem (1+ nrem))
            (if cur (setq pcs (cons cur pcs)))
            (setq cur nil))))))
  (if cur (setq pcs (cons cur pcs)))
  (list nrem (reverse pcs)))

;; sobras de duplicatas: o que cair dentro do circulo do no n e lixo
;; (e o que causava "ganchos" no entroncamento); so se guarda o que
;; fica fora do circulo
(defun v:trim-stub (s rpts n / r)
  (setq r (v:trimpieces (nth 1 s) rpts))
  (foreach pc (cadr r)
    (foreach op (v:outpieces pc n *vR*)
      (v:mkpoly (car s) op))))

;; limpa, ao longo de spts, as entidades que correm paralelas
(defun v:cleanup (spts / ss i e pts r ly)
  (if (setq ss (v:bbox-ss spts *vT*))
    (progn
      (setq i 0)
      (repeat (sslength ss)
        (setq e (ssname ss i) i (1+ i))
        (if (not (member e *vDone*))
          (progn
            (setq pts (v:pts e))
            (if (and pts (cdr pts))
              (progn
                (setq r (v:trimpieces pts spts))
                (if (> (car r) 0)
                  (progn
                    (setq ly (cdr (assoc 8 (entget e))))
                    (foreach pc (cadr r) (v:mkpoly ly pc))
                    (entdel e)))))))))))

;; --- proxima interseccao provavel ao longo de spts ---------------------
;; retorna (distancia-ao-longo indice-do-segmento ponto-J) ou nil
(defun v:scan (spts / ss i e pts best tot lim q nr idx j s ang rang z op
                      a b ep1 sp sidx ip far)
  (setq tot (v:len spts) lim (* *vA2* (/ pi 180.0)))
  ;; 1) pontas de outras vias que chegam e se afastam do alinhamento
  (if (setq ss (v:bbox-ss spts *vT*))
    (progn
      (setq i 0)
      (repeat (sslength ss)
        (setq e (ssname ss i) i (1+ i) pts (v:pts e))
        (if (and pts (cdr pts))
          (foreach op (list pts (reverse pts))
            (setq q (car op) nr (v:near q spts))
            (if (<= (car nr) *vT*)
              (progn
                (setq idx (cadr nr)
                      j (v:proj q (nth idx spts) (nth (1+ idx) spts))
                      s (+ (v:len (v:take (1+ idx) spts))
                           (distance (nth idx spts) j))
                      z (v:ahead-pt op (* 2.0 *vT*))
                      ang (if (> (distance q (v:ahead-pt op 10.0)) 1e-9)
                            (angle q (v:ahead-pt op 10.0))
                            nil)
                      rang (angle (nth idx spts) (nth (1+ idx) spts)))
                (if (and ang
                         (>= s *vR*)
                         (<= s (- tot *vT*))
                         (> (car (v:near z spts)) *vT*)
                         (>= (v:pdiff ang rang) lim)
                         (or (null best) (< s (car best))))
                  (setq best (list s idx j))))))))))
  ;; 2) vias que CRUZAM o alinhamento em angulo relevante
  (if (setq ss (ssget "_F" spts '((0 . "LINE,LWPOLYLINE"))))
    (progn
      (setq i 0)
      (repeat (sslength ss)
        (setq e (ssname ss i) i (1+ i) pts (v:pts e))
        (if (and pts (cdr pts))
          (progn
            (setq far (apply 'max
                             (mapcar '(lambda (q2) (car (v:near q2 spts))) pts)))
            (if (> far *vT*)
              (progn
                (setq ep1 pts)
                (while (cdr ep1)
                  (setq a (car ep1) b (cadr ep1) ep1 (cdr ep1)
                        sp spts sidx 0)
                  (while (cdr sp)
                    (setq ip (inters a b (car sp) (cadr sp) T))
                    (if ip
                      (progn
                        (setq s (+ (v:len (v:take (1+ sidx) spts))
                                   (distance (nth sidx spts) ip)))
                        (if (and (>= s *vR*)
                                 (<= s (- tot *vT*))
                                 (>= (v:pdiff (angle a b)
                                              (angle (car sp) (cadr sp)))
                                     lim)
                                 (or (null best) (< s (car best))))
                          (setq best (list s sidx (list (car ip) (cadr ip)))))))
                    (setq sp (cdr sp) sidx (1+ sidx)))))))))))
  best)

;; --- caminhada ---------------------------------------------------------------
;; stop = (ponto cor) da marca final; cores: 2 amarelo, 4 ciano, 6 magenta
(defun v:walk (p rpts ly / kprev done steps spts sc head tail ep g ents stubs
                           cl bb back fwd tol cc rep2 pf kold en c s2 ee stop)
  (setq kprev 0 done nil steps 0 stop nil tol (* *vA* (/ pi 180.0)))
  (while (not done)
    (setq spts (v:nthcdr kprev rpts))
    (v:cleanup spts)
    (setq sc (v:scan spts))
    (cond
      ;; interseccao provavel no meio do trecho
      (sc
       (setq head (append (v:take (1+ (cadr sc)) spts) (list (caddr sc)))
             tail (v:dedup (cons (caddr sc) (v:nthcdr (1+ (cadr sc)) spts)))
             rpts (v:dedup (append (v:take kprev rpts) head)))
       (v:mkpoly ly tail)
       (setq stop (list (last rpts) 2) done T))
      ;; analise na ponta do trecho
      (T
       (setq ep (last rpts)
             g (v:gather ep *vT*)
             ents (car g)
             stubs (cadr g)
             bb (v:backbearing rpts ep)
             cl (v:cluster stubs *vA*)
             back nil
             fwd nil)
       (foreach c cl
         (if (v:backp c bb tol)
           (setq back (append back c))
           (setq fwd (cons c fwd))))
       (cond
         ((>= (length fwd) 2)
          (setq stop (list ep 2) done T))
         ((= (length fwd) 0)
          (setq stop (list ep 4) done T))
         (T
          ;; continuacao: une pela geometria real do ramo de saida
          (setq cc (v:sortcl (car fwd))
                rep2 (car cc)
                pf (nth 1 rep2)
                kold (1- (length rpts))
                rpts (v:dedup (append rpts pf)))
          (foreach s2 (append (cdr cc) back) (v:trim-stub s2 rpts ep))
          (foreach ee ents (entdel ee))
          (setq kprev kold steps (1+ steps))
          (if (or (> steps 300)
                  (and (> steps 2) (< (distance (last rpts) p) *vR*)))
            (setq stop (list (last rpts) 6) done T)))))))
  (setq en (v:mkpoly ly rpts))
  (if en (setq *vDone* (cons en *vDone*)))
  (v:clear-marks rpts *vT* T)
  (if stop (v:mark (car stop) (cadr stop)))
  rpts)

;; --- clique perto de um entroncamento -------------------------------------
(defun v:resolve (p / g stubs cl reps r n avg ents cc rep rpts0 jobs c s2 j e2)
  ;; fase 1: ramos perto do clique -> interseccao real
  (setq g (v:gather p *vC*) stubs (cadr g))
  (setq cl (v:cluster stubs *vA*))
  (if (< (length cl) 2)
    (progn
      (v:mark p 1)
      (princ "\nMenos de 2 ramos perto do clique: nada alterado (VERMELHO = analisar)."))
    (progn
      (setq reps (mapcar '(lambda (c) (car (v:sortcl c))) cl))
      (setq r (v:findnode p reps) n (car r) avg (cadr r))
      (if (> avg *vT*)
        (progn
          (v:mark p 1)
          (princ "\nNao achei uma interseccao coerente perto do clique: nada alterado (VERMELHO = analisar)."))
        (progn
          ;; fase 2: reagrupa os ramos ao redor da interseccao encontrada
          (setq g (v:gather n *vT*) ents (car g) stubs (cadr g))
          (setq cl (v:cluster stubs *vA*))
          (if (< (length cl) 2)
            (progn
              (v:mark p 1)
              (princ "\nInterseccao sem ramos suficientes: nada alterado (VERMELHO = analisar)."))
            (progn
              (v:clear-marks (list p p) *vR* nil)
              (v:clear-marks (list n n) *vR* nil)
              (foreach c cl
                (setq cc (v:sortcl c)
                      rep (car cc)
                      rpts0 (v:dedup (cons n (nth 1 rep))))
                (foreach s2 (cdr cc) (v:trim-stub s2 rpts0 n))
                (setq jobs (cons (list (nth 0 rep) rpts0) jobs)))
              (foreach e2 ents (entdel e2))
              (v:mark n 3)
              (foreach j (reverse jobs)
                (v:walk n (cadr j) (car j)))
              (princ (strcat "\nEntroncamento resolvido: " (itoa (length cl)) " ramos.")))))))))

;; --- comandos ------------------------------------------------------------------
(defun c:VIAUCLR ( / ss)
  (if (setq ss (ssget "_X" '((0 . "CIRCLE") (8 . "_REVISAO"))))
    (progn
      (command "_.erase" ss "")
      (princ "\nMarcas da layer _REVISAO apagadas.")))
  (princ))

(defun c:VIAU ( / doc p r)
  (vl-load-com)
  (if (null *vR*) (setq *vR* 40.0))
  (if (null *vT*) (setq *vT* 16.0))
  (if (null *vA*) (setq *vA* 25.0))
  (if (null *vA2*) (setq *vA2* 35.0))
  (if (null *vC*) (setq *vC* 25.0))
  (setq r (getreal (strcat "\nRaio de busca no entroncamento (m) <" (rtos *vR* 2 1) ">: ")))
  (if r (setq *vR* r))
  (setq r (getreal (strcat "\nTolerancia de distancia entre duplicatas (m) <" (rtos *vT* 2 1) ">: ")))
  (if r (setq *vT* r))
  (setq r (getreal (strcat "\nAngulo max. para agrupar ramos no no (graus) <" (rtos *vA* 2 1) ">: ")))
  (if r (setq *vA* r))
  (setq r (getreal (strcat "\nAngulo max. para considerar paralelo / mesma via (graus) <" (rtos *vA2* 2 1) ">: ")))
  (if r (setq *vA2* r))
  (setq r (getreal (strcat "\nRaio de captura ao redor do clique (m) <" (rtos *vC* 2 1) ">: ")))
  (if r (setq *vC* r))
  (princ "\nMARCAS: VERDE=resolvido | AMARELO=possivel entroncamento (clique) | VERMELHO=analisar | MAGENTA=limite/loop | CIANO=fim de via")
  (setq doc (vla-get-activedocument (vlax-get-acad-object)))
  (while (setq p (getpoint "\nClique no entroncamento <Enter sai>: "))
    (vla-startundomark doc)
    (setq r (vl-catch-all-apply 'v:resolve (list p)))
    (vla-endundomark doc)
    (if (vl-catch-all-error-p r)
      (progn
        (princ (strcat "\nErro: " (vl-catch-all-error-message r)
                       " -> desfazendo este clique."))
        (command "_.undo" "1"))))
  (princ))

(princ "\nVIAU v0.4 carregado. Comandos: VIAU, VIAUCLR.")
(princ)
