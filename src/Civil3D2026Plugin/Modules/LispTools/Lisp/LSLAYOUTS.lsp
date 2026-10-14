;;@TITULO  Layer state em varios layouts
;;@CATEGORIA Pranchas e layouts
;;@COMANDO LSLAYOUTS
;;@VERSAO  1.0
;;@DESC    Restaura um layer state como override de viewport (VP Freeze, VP cor etc.)
;;@DESC    em todos os viewports dos layouts escolhidos, de uma unica vez.
;;@DESC    O layer state precisa ter sido salvo de dentro de um viewport (MSPACE).
;;@USO     Digite LSLAYOUTS e informe o nome do layer state
;;@USO     Informe o filtro de layouts com curinga (ex.: PR-*) ou Enter para todos

(defun c:LSLAYOUTS (/ ls pat ss i ent n)
  (setq ls (getstring T "\nNome do layer state: "))
  (cond
    ((not (layerstate-has ls))
     (princ "\nLayer state nao encontrado."))
    (T
     (setq pat (getstring "\nLayouts (curinga, ex. PR-*; Enter = todos): "))
     (if (= pat "") (setq pat "*"))
     (setq n 0)
     (foreach lay (layoutlist)
       (if (and (wcmatch (strcase lay) (strcase pat))
                (setq ss (ssget "_X" (list '(0 . "VIEWPORT") (cons 410 lay)))))
         (repeat (setq i (sslength ss))
           (setq ent (ssname ss (setq i (1- i))))
           ;; ignora o viewport de papel (ID 1)
           (if (/= 1 (cdr (assoc 69 (entget ent))))
             (progn
               (layerstate-restore ls ent 4) ; 4 = restaurar como VP override
               (setq n (1+ n)))))))
     (command "_.REGENALL")
     (princ (strcat "\n" (itoa n) " viewport(s) atualizado(s).")))
  )
  (princ)
)
(princ)
