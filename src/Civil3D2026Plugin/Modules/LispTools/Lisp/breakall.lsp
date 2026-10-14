;;@TITULO  Quebrar nas intersecoes
;;@CATEGORIA Polilinhas e geometria
;;@COMANDO BREAKALL
;;@COMANDO BREAKOBJECT
;;@COMANDO BREAKWOBJECTS
;;@COMANDO BREAKWITH
;;@COMANDO BREAKTOUCHING
;;@COMANDO BREAKSELECTED
;;@VERSAO  1.3
;;@DESC    Quebra linhas, polilinhas, splines, elipses, circulos e arcos nos pontos de intersecao.
;;@DESC    BREAKALL quebra todos entre si; as outras variantes quebram por um objeto, com um objeto ou
;;@DESC    so os que se tocam. Objetos precisam ter o mesmo Z. Autor: Charles Alan Butler.
;;@USO     Digite BREAKALL e selecione os objetos
;; ----------------------------------------------------------------
;;;=======================[ BreakObjects.lsp ]==============================
;;; Author: Copyright© 2006,2007 Charles Alan Butler 
;;; Contact @  www.TheSwamp.org
;;; Version:  1.3 April 9,2007
;;; Globalization by XANADU - www.xanadu.cz
;;; Purpose: Break All selected objects
;;;    permitted objects are lines, lwplines, plines, splines,
;;;    ellipse, circles & arcs 
;;;                            
;;;  Function  c:BreakAll -      Break all objects selected
;;;  Function  c:BreakwObjects - Break many objects with a single object
;;;  Function  c:BreakObject -   Break a single object with many objects 
;;;  Function  c:BreakWith -     Break selected objects with other selected objects
;;;  Function  c:BreakTouching - Break objects touching the single Break object
;;;  Function  c:BreakSelected - Break selected objects with any  objects that touch it 
;;;                    
;;; Sub_Routines:      
;;;    break_with      
;;;    ssget->vla-list 
;;;    list->3pair     
;;;    onlockedlayer   
;;;    get_interpts Return a list of intersect points
;;;    break_obj  Break entity at break points in list
;;; Requirements: objects must have the same z-value
;;; Restrictions: Does not Break objects on locked layers 
;;; Returns:  none
;;;=====================================================================
;;;   THIS SOFTWARE IS PROVIDED "AS IS" WITHOUT EXPRESS OR IMPLIED     ;
;;;   WARRANTY.  ALL IMPLIED WARRANTIES OF FITNESS FOR ANY PARTICULAR  ;
;;;   PURPOSE AND OF MERCHANTABILITY ARE HEREBY DISCLAIMED.            ;
;;;                                                                    ;
;;;  You are hereby granted permission to use, copy and modify this    ;
;;;  software without charge, provided you do so exclusively for       ;
;;;  your own use or for use by others in your organization in the     ;
;;;  performance of their normal duties, and provided further that     ;
;;;  the above copyright notice appears in all copies and both that    ;
;;;  copyright notice and the limited warranty and restricted rights   ;
;;;  notice below appear in all supporting documentation.              ;
;;;=====================================================================


;;+++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++
;;               M A I N   S U B R O U T I N E                   
;;+++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++

(defun break_with (ss2brk ss2brkwith self / cmd intpts lst masterlist ss ssobjs
                   onlockedlayer ssget->vla-list list->3pair
                   get_interpts break_obj
                  )
  ;; ss2brk     selection set to break
  ;; ss2brkwith selection set to use as break points
  ;; self       when true will allow an object to break itself
  ;;            note that plined will break at each vertex
  (vl-load-com)


;;+++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++
;;                S U B   F U N C T I O N S                      
;;+++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++

  (defun onlockedlayer (ename / entlst)
    (setq entlst (tblsearch "LAYER" (cdr (assoc 8 (entget ename)))))
    (= 4 (logand 4 (cdr (assoc 70 entlst))))
  )
  
  (defun ssget->vla-list (ss / i ename lst)
    (setq i -1)
    (while (setq ename (ssname ss (setq i (1+ i))))
      (setq lst (cons (vlax-ename->vla-object ename) lst))
    )
    lst
  )

  (defun list->3pair (old / new)
    (while (setq new (cons (list (car old) (cadr old) (caddr old)) new)
                 old (cdddr old))
    )
    (reverse new)
  )
  
;;==============================================================
;;  return a list of intersect points
;;==============================================================
(defun get_interpts (obj1 obj2 / iplist)
  (if (not (vl-catch-all-error-p
             (setq iplist (vl-catch-all-apply
                            'vlax-safearray->list
                            (list
                              (vlax-variant-value
                                (vla-intersectwith obj1 obj2 acextendnone)
                              ))))))
    iplist
  )
)


;;==============================================================
;;  Break entity at break points in list
;;==============================================================
(defun break_obj (ent brkptlst / brkobjlst en enttype maxparam closedobj
                  minparam obj obj2break p1param p2 p2param
                 )

  (setq obj2break ent
        brkobjlst (list ent)
        enttype   (cdr (assoc 0 (entget ent)))
  )

  (foreach brkpt brkptlst
    ;;  get last entity created via break in case multiple breaks
    (if brkobjlst
      (progn
        ;;  if pt not on object x, switch objects
        (if (not (numberp (vl-catch-all-apply 'vlax-curve-getdistatpoint (list obj2break brkpt)))
            )
          (foreach obj brkobjlst ; find the one that pt is on
            (if (numberp (vl-catch-all-apply 'vlax-curve-getdistatpoint (list obj brkpt)))
              (setq obj2break obj) ; switch objects
            )
          )
        )
      )
    )

    ;;  Handle any objects that can not be used with the Break Command
    ;;  using one point, gap of 0.000001 is used
    (cond
      ((and (= "SPLINE" enttype) ; only closed splines
            (vlax-curve-isclosed obj2break))
       (setq p1param (vlax-curve-getparamatpoint obj2break brkpt)
             p2      (vlax-curve-getpointatparam obj2break (+ p1param 0.000001))
       )
       (command "._break" obj2break "_non" (trans brkpt 0 1) "_non" (trans p2 0 1))
      )
      ((= "CIRCLE" enttype) ; break the circle
       (setq p1param (vlax-curve-getparamatpoint obj2break brkpt)
             p2      (vlax-curve-getpointatparam obj2break (+ p1param 0.000001))
       )
       (command "._break" obj2break "_non" (trans brkpt 0 1) "_non" (trans p2 0 1))
       (setq enttype "ARC")
      )
      ((and (= "ELLIPSE" enttype) ; only closed ellipse
            (vlax-curve-isclosed obj2break))
       ;;  Break the ellipse, code borrowed from Joe Burke  6/6/2005
       (setq p1param  (vlax-curve-getparamatpoint obj2break brkpt)
             p2param  (+ p1param 0.000001)
             minparam (min p1param p2param)
             maxparam (max p1param p2param)
             obj      (vlax-ename->vla-object obj2break)
       )
       (vlax-put obj 'startparameter maxparam)
       (vlax-put obj 'endparameter (+ minparam (* pi 2)))
      )
      
      ;;==================================
      (t  ;   Objects that can be broken     
       (setq closedobj (vlax-curve-isclosed obj2break))
       (command "._break" obj2break "_non" (trans brkpt 0 1) "_non" (trans brkpt 0 1))
       (if (not closedobj) ; new object was created
           (setq brkobjlst (cons (entlast) brkobjlst))
       )
      )
    )
  )
)


  
  ;;+++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++
  ;;                   S T A R T   H E R E                         
  ;;+++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++
    (if (and ss2brk ss2brkwith)
    (progn
      ;;  CREATE a list of entity & it's break points
      (foreach obj (ssget->vla-list ss2brk) ; check each object in ss2brk
        (if (not (onlockedlayer (vlax-vla-object->ename obj)))
          (progn
            (setq lst nil)
            ;; check for break pts with other objects in ss2brkwith
            (foreach intobj (ssget->vla-list ss2brkwith) 
              (if (and (or self (not (equal obj intobj)))
                       (setq intpts (get_interpts obj intobj))
                  )
                (setq lst (append (list->3pair intpts) lst)) ; entity w/ break points
              )
            )
            (if lst
              (setq masterlist (cons (cons (vlax-vla-object->ename obj) lst) masterlist))
            )
          )
        )
      )
      ;;  masterlist = ((ent brkpts)(ent brkpts)...)
      (if masterlist
        (foreach obj2brk masterlist
          (break_obj (car obj2brk) (cdr obj2brk))
        )
      )
      )
  )
;;==============================================================

)
(prompt "\nBreak Routines Loaded, Enter BreakAll, BreakEnt, or BreakWith to run.")
(princ)



;;==========================================
;;        Break all objects selected        
;;==========================================
(defun c:breakall (/ cmd ss)

  (command "._undo" "_begin")
  (setq cmd (getvar "CMDECHO"))
  (setvar "CMDECHO" 0)

  ;;  get objects to break
  (prompt "\nSelect All objects to break & press enter: ")
  (if (setq ss (ssget '((0 . "LINE,ARC,SPLINE,LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE"))))
     (Break_with ss ss nil) ; ss2break ss2breakwith (flag nil = not to break with self)
  )

  (setvar "CMDECHO" cmd)
  (command "._undo" "_end")
  (princ)
)


;;==========================================
;;  Break a single object with many objects 
;;==========================================
(defun c:BreakObject (/ cmd ss1 ss2)

  (command "._undo" "_begin")
  (setq cmd (getvar "CMDECHO"))
  (setvar "CMDECHO" 0)

  ;;  get objects to break
  (prompt "\nSelect single object to break: ")
  (if (and (setq ss1 (ssget "+.:E:S" '((0 . "LINE,ARC,SPLINE,LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE"))))
           (not (redraw (ssname ss1 0) 3))
           (not (prompt "\n***  Select object(s) to break with & press enter:  ***"))
           (setq ss2 (ssget '((0 . "LINE,ARC,SPLINE,LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE"))))
           (not (redraw (ssname ss1 0) 4)))
     (Break_with ss1 ss2 nil) ; ss2break ss2breakwith (flag nil = not to break with self)
  )

  (setvar "CMDECHO" cmd)
  (command "._undo" "_end")
  (princ)
)

;;==========================================
;;  Break many objects with a single object 
;;==========================================
(defun c:breakwobjects (/ cmd ss1 ss2)
  (defun ssredraw (ss mode / i num)
    (setq i -1)
    (while (setq ename (ssname ss (setq i (1+ i))))
      (redraw (ssname ss i) mode)
    )
  )
  (command "._undo" "_begin")
  (setq cmd (getvar "CMDECHO"))
  (setvar "CMDECHO" 0)

  ;;  get objects to break
  (prompt "\nSelect object(s) to break & press enter: ")
  (if (and (setq ss1 (ssget '((0 . "LINE,ARC,SPLINE,LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE"))))
           (not (ssredraw ss1 3))
           (not (prompt "\n***  Select single object to break with:  ***"))
           (setq ss2 (ssget "+.:E:S" '((0 . "LINE,ARC,SPLINE,LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE"))))
           (not (ssredraw ss1 4))
      )
    (break_with ss1 ss2 nil) ; ss2break ss2breakwith (flag nil = not to break with self)
  )

  (setvar "CMDECHO" cmd)
  (command "._undo" "_end")
  (princ)
)

;;==========================================
;;  Break many objects with many object     
;;==========================================
(defun c:BreakWith (/ cmd ss1 ss2)
  (defun ssredraw (ss mode / i num)
    (setq i -1)
    (while (setq ename (ssname ss (setq i (1+ i))))
      (redraw (ssname ss i) mode)
    )
  )
  (command "._undo" "_begin")
  (setq cmd (getvar "CMDECHO"))
  (setvar "CMDECHO" 0)

  ;;  get objects to break
  (prompt "\nSelect object(s) to break & press enter: ")
  (if (and (setq ss1 (ssget '((0 . "LINE,ARC,SPLINE,LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE"))))
           (not (ssredraw ss1 3))
           (not (prompt "\n***  Select object(s) to break with & press enter:  ***"))
           (setq ss2 (ssget '((0 . "LINE,ARC,SPLINE,LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE"))))
           (not (ssredraw ss1 4))
      )
    (break_with ss1 ss2 nil) ; ss2break ss2breakwith (flag nil = not to break with self)
  )

  (setvar "CMDECHO" cmd)
  (command "._undo" "_end")
  (princ)
)



;;=============================================
;;  Break many objects with a selected objects 
;;  Selected Objects create ss to be broken    
;;=============================================

(defun c:BreakTouching (/ cmd ss1 ss2)
  
  ;;  get all objects touching entities in the sscross
  ;;  limited obj types to "LINE,ARC,SPLINE,LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE"
  (defun gettouching (sscros / ss lst lstb lstc objl)
    (and
      (setq lstb (vl-remove-if 'listp (mapcar 'cadr (ssnamex sscros)))
            objl (mapcar 'vlax-ename->vla-object lstb)
      )
      (setq
        ss (ssget "_A" (list (cons 0 "LINE,ARC,SPLINE,LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE")
                             (cons 410 (getvar "ctab"))))
      )
      (setq lst (vl-remove-if 'listp (mapcar 'cadr (ssnamex ss))))
      (setq lst (mapcar 'vlax-ename->vla-object lst))
      (mapcar
        '(lambda (x)
           (mapcar
             '(lambda (y)
                (if (not
                      (vl-catch-all-error-p
                        (vl-catch-all-apply
                          '(lambda ()
                             (vlax-safearray->list
                               (vlax-variant-value
                                 (vla-intersectwith y x acextendnone)
                               ))))))
                  (setq lstc (cons (vlax-vla-object->ename x) lstc))
                )
              ) objl)
         ) lst)
    )
    lstc
  )

  (command "._undo" "_begin")
  (setq cmd (getvar "CMDECHO"))
  (setvar "CMDECHO" 0)
  (setq ss1 (ssadd))
  ;;  get objects to break
  (if (and (not (prompt "\nSelect object(s) to break with & press enter: "))
           (setq ss2 (ssget '((0 . "LINE,ARC,SPLINE,LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE"))))
           (mapcar '(lambda (x) (ssadd x ss1)) (gettouching ss2))
      )
    (break_with ss1 ss2 nil) ; ss2break ss2breakwith (flag nil = not to break with self)
  )

  (setvar "CMDECHO" cmd)
  (command "._undo" "_end")
  (princ)
)



;;==========================================================
;;  Break selected objects with any objects that touch it  
;;==========================================================


(defun c:BreakSelected (/ cmd ss1 ss2)
  
  ;;  get all objects touching entities in the sscross
  ;;  limited obj types to "LINE,ARC,SPLINE,LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE"
  (defun gettouching (sscros / ss lst lstb lstc objl)
    (and
      (setq lstb (vl-remove-if 'listp (mapcar 'cadr (ssnamex sscros)))
            objl (mapcar 'vlax-ename->vla-object lstb)
      )
      (setq
        ss (ssget "_A" (list (cons 0 "LINE,ARC,SPLINE,LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE")
                             (cons 410 (getvar "ctab"))))
      )
      (setq lst (vl-remove-if 'listp (mapcar 'cadr (ssnamex ss))))
      (setq lst (mapcar 'vlax-ename->vla-object lst))
      (mapcar
        '(lambda (x)
           (mapcar
             '(lambda (y)
                (if (not
                      (vl-catch-all-error-p
                        (vl-catch-all-apply
                          '(lambda ()
                             (vlax-safearray->list
                               (vlax-variant-value
                                 (vla-intersectwith y x acextendnone)
                               ))))))
                  (setq lstc (cons (vlax-vla-object->ename x) lstc))
                )
              ) objl)
         ) lst)
    )
    lstc
  )

  (command "._undo" "_begin")
  (setq cmd (getvar "CMDECHO"))
  (setvar "CMDECHO" 0)
  (setq ss1 (ssadd))
  ;;  get objects to break
  (if (and (not (prompt "\nSelect object(s) to break with & press enter: "))
           (setq ss2 (ssget '((0 . "LINE,ARC,SPLINE,LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE"))))
           (mapcar '(lambda (x) (ssadd x ss1)) (gettouching ss2))
      )
    (break_with ss2 ss1 nil) ; ss2break ss2breakwith (flag nil = not to break with self)
  )

  (setvar "CMDECHO" cmd)
  (command "._undo" "_end")
  (princ)
)


;;/'\./'\./'\./'\./'\./'\./'\./'\./'\./'\./'\./'\./'\./'\./'\./'\.
;;    E n d   O f   F i l e   I f   y o u   A r e   H e r e       
;;/'\./'\./'\./'\./'\./'\./'\./'\./'\./'\./'\./'\./'\./'\./'\./'\.