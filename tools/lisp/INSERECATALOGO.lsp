;;; INSERECATALOGO.lsp - Civil 3D 2026
;;; Insere numa rede nova uma estrutura de cada tamanho de cada familia
;;; de uma Parts List, em grade, com o nome da familia/tamanho ao lado.
;;; Uso: APPLOAD este arquivo, depois o comando INSERECATALOGO.
;;; Antes: crie uma Parts List com todas as familias do catalogo
;;; (Settings > Pipe Network > Parts Lists > Structures > Add part family,
;;; marque todas). So os tamanhos que estiverem na Parts List entram.

(vl-load-com)

(defun ic:pipeapp (/ acad app)
  (setq acad (vlax-get-acad-object))
  (foreach v '("13.8" "13.9" "13.7")
    (if (null app)
      (progn
        (setq app (vl-catch-all-apply 'vla-getinterfaceobject
                    (list acad (strcat "AeccXUiPipe.AeccPipeApplication." v))))
        (if (vl-catch-all-error-p app) (setq app nil))
      )
    )
  )
  app
)

(defun ic:texto (pt txt h)
  (entmake (list '(0 . "TEXT") '(8 . "REF-CATALOGO") (cons 10 pt) (cons 40 h) (cons 1 txt)))
)

(defun c:INSERECATALOGO (/ app pdoc pls nl i nomes nome pl net strs nfam fam szs nsz j sz
                           dx dy x y res ok falhas pt)

  (if (not (setq app (ic:pipeapp)))
    (progn (princ "\nNao achei a API de Pipe Network do Civil 3D.") (exit))
  )
  ;; as Parts Lists ficam em Settings (AeccPipeSettingsRoot.PartLists), nao no documento
  (setq pdoc (vlax-get app 'ActiveDocument)
        pls  (vlax-get (vlax-get pdoc 'Settings) 'PartLists)
        nl   (vlax-get pls 'Count)
        i    0)
  (princ "\nParts Lists do desenho:")
  (while (< i nl)
    (setq nome (vlax-get (vlax-invoke-method pls 'Item i) 'Name)
          nomes (append nomes (list nome)))
    (princ (strcat "\n  " (itoa (1+ i)) " - " nome))
    (setq i (1+ i))
  )
  (setq i (getint (strcat "\nNumero da Parts List <1>: ")))
  (if (or (null i) (< i 1) (> i nl)) (setq i 1))
  (setq pl (vlax-invoke-method pls 'Item (1- i)))
  ;; padrao de 8 m: 8000 em desenho mm (INSUNITS 4), 8 nos demais
  (setq dx (if (= (getvar "INSUNITS") 4) 8000.0 8.0))
  (setq dx (cond ((getdist (strcat "\nEspacamento da grade <" (rtos dx 2 0) ">: "))) (dx)))
  (setq dy dx)
  (setq net (vlax-invoke-method (vlax-get pdoc 'PipeNetworks) 'Add
              (strcat "REF-CATALOGO-" (menucmd "M=$(edtime,$(getvar,date),HHMMSS)")))
        strs (vlax-get net 'Structures)
        nfam (vlax-get pl 'Count)
        y 0.0 i 0 ok 0 falhas 0)
  (while (< i nfam)
    (setq fam (vlax-invoke-method pl 'Item i)
          szs (vl-catch-all-apply 'vlax-get (list fam 'SizeFilters)))
    ;; so familias de estrutura (Domain 2 = aeccDomStructure); tubos ficam de fora
    (if (and (not (vl-catch-all-error-p szs)) (= (vlax-get fam 'Domain) 2))
      (progn
        (setq nsz (vlax-get szs 'Count) j 0 x 0.0 res nil)
        (while (< j nsz)
          (setq sz  (vlax-invoke-method szs 'Item j)
                pt  (list x y 0.0)
                res (vl-catch-all-apply 'vlax-invoke-method
                      (list strs 'Add (vlax-get fam 'GUID) sz (vlax-3d-point pt) 0.0)))
          (if (vl-catch-all-error-p res)
            (progn                        ; tamanho invalido: pula e mostra o motivo
              (setq falhas (1+ falhas))
              (princ (strcat "\n  pulado: " (vlax-get fam 'Name) " | " (vlax-get sz 'Name)
                             " -> " (vl-catch-all-error-message res)))
            )
            (progn
              (setq ok (1+ ok))
              (ic:texto (list (- x (* 0.4 dx)) (- y (* 0.35 dy)) 0.0)
                        (strcat (vlax-get fam 'Name) " | " (vlax-get sz 'Name))
                        (* 0.03 dx))
            )
          )
          (setq x (+ x dx) j (1+ j))
        )
        (if (> j 0) (setq y (- y dy)))
      )
    )
    (setq i (1+ i))
  )
  (princ (strcat "\nInseridas " (itoa ok) " estruturas na rede "
                 (vlax-get net 'Name) " (" (itoa falhas) " itens pulados)."))
  (command "_.ZOOM" "_E")
  (princ)
)

(princ "\nINSERECATALOGO carregado. Digite INSERECATALOGO.")
(princ)
