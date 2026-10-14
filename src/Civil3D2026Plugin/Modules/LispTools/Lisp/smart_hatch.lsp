;;@TITULO  Hachura ignorando gaps
;;@CATEGORIA Hachuras
;;@COMANDO SMARTHATCH
;;@DESC    Cria uma hachura SOLID clicando dentro de uma area cujo contorno tem pequenas aberturas: gera o
;;@DESC    contorno com tolerancia de gap alta (HPGAPTOL 50), hachura e apaga o contorno temporario.
;;@USO     Digite SMARTHATCH
;;@USO     Clique dentro da area
;; ----------------------------------------------------------------
(defun c:SmartHatch ( / oldGap oldCmd pt ss boundaryEnt)
  (setq oldGap (getvar "HPGAPTOL")
        oldCmd (getvar "CMDECHO"))
  
  (setvar "CMDECHO" 0)
  
  ;; Define uma tolerância alta temporária (ex: 50 unidades de desenho)
  ;; Mude o valor abaixo se o seu gap for ainda maior na escala real
  (setvar "HPGAPTOL" 50.0) 
  
  (setq pt (getpoint "\nClique dentro da área exibida para hachurar: "))
  
  (if pt
    (progn
      (princ "\nAnalisando área da tela e gerando contorno inteligente...")
      
      ;; Tenta gerar o contorno ignorando os gaps da janela visível
      (command "._-boundary" pt "")
      
      (setq boundaryEnt (entlast))
      
      ;; Verifica se uma nova polilinha foi criada com sucesso
      (if (and boundaryEnt (= (cdr (assoc 0 (entget boundaryEnt))) "LWPOLYLINE"))
        (progn
          ;; Aplica a hachura diretamente selecionando o objeto gerado
          ;; Altere "SOLID" para o padrão desejado (Ex: "ANSI31") se necessário
          (command "._-hatch" "_Properties" "SOLID" "_Select" boundaryEnt "" "")
          
          ;; Apaga a polilinha temporária para limpar o desenho
          (entdel boundaryEnt)
          (princ "\nHachura aplicada com sucesso ignorando os gaps!")
        )
        (princ "\nErro: Não foi possível determinar o contorno mesmo com tolerância. Verifique o tamanho do gap.")
      )
    )
  )
  
  ;; Restaura as configurações originais do AutoCAD do usuário
  (setvar "HPGAPTOL" oldGap)
  (setvar "CMDECHO" oldCmd)
  (princ)
)
