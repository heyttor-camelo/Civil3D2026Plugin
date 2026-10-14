;;@TITULO  Cores ACI para RGB
;;@CATEGORIA Blocos, textos e cores
;;@COMANDO ALLTORGB
;;@DESC    Converte a cor indice (ACI) dos objetos selecionados para a cor RGB (TrueColor) equivalente.
;;@DESC    Util antes de exportar ou enviar o desenho, para a cor nao depender da paleta. Autor: Lee Mac.
;;@USO     Digite ALLTORGB
;;@USO     Selecione os objetos (layers travadas sao ignoradas)
;; ----------------------------------------------------------------
;; All to RGB  -  Lee Mac  -  www.lee-mac.com
;; Converts the ACI colours of all entities to the RGB TrueColor equivalent

(defun c:AlltoRGB ( / accm c e i s )
   (if
       (and
           (setq s (ssget "_:L"))
           (setq accm
               (vla-getinterfaceobject (vlax-get-acad-object)
                   (strcat "AutoCAD.AcCmColor." (substr (getvar 'ACADVER) 1 2))
               )
           )
       )
       (progn
           (repeat (setq i (sslength s))
               (setq e (entget (ssname s (setq i (1- i)))))
               (if (setq c (cdr (assoc 62 e)))
                   (progn
                       (vla-put-colorindex accm c)
                       (entmod
                           (append e
                               (list
                                   (cons 420
                                       (LM:RGB->True
                                           (vla-get-red   accm)
                                           (vla-get-green accm)
                                           (vla-get-blue  accm)
                                       )
                                   )
                               )
                           )
                       )
                   )
               )
           )
           (vlax-release-object accm)
       )
   )
   (princ)
)

;; RGB -> True - Lee Mac 2011
;; Args: r,g,b - Red,Green,Blue values

(defun LM:RGB->True ( r g b )
   (+
       (lsh (fix r) 16)
       (lsh (fix g)  8)
       (fix b)
   )
)

(vl-load-com) (princ)