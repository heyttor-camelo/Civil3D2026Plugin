using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Civil.ApplicationServices;

using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.UI.Common;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.Modules.QTO
{
    /// <summary>
    /// Painel do QTO (C3DQTO): lista os corredores do desenho, deixa escolher o eixo das estacas de
    /// cada um, o intervalo e a tolerancia, e manda os comandos QTOESTUDO / QTOTODOS ja com tudo
    /// escolhido (o trabalho roda dentro do comando, o jeito seguro do AutoCAD). So le o desenho.
    /// </summary>
    internal sealed class QtoPanel : SW.Window
    {
        private static QtoPanel? _instance;

        /// <summary>Um corredor na lista, com os eixos (Alignments) das baselines dele.</summary>
        private sealed class Linha
        {
            public AcadDb.ObjectId Corridor;
            public string Nome = string.Empty;
            public List<AcadDb.ObjectId> Eixos = new();
            public List<string> NomesEixos = new();
            public WC.CheckBox Marca = new();
            public WC.ComboBox? Escolha;
        }

        private readonly ToolTheme T = ToolTheme.Current;
        private readonly List<Linha> _linhas = new();
        private readonly WC.StackPanel _lista = new();
        private readonly WC.TextBlock _desenho = new();
        private readonly WC.TextBlock _aviso = new();
        private readonly WC.TextBox _intervalo = new();
        private readonly WC.TextBox _tolerancia = new();
        private readonly WC.CheckBox _abrirPasta = new();
        private Document? _doc;

        private QtoPanel()
        {
            LogView.Atalho(this, "QTO");
            ToolStyles.Aplicar(Resources, T);

            Title = "Quantitativos (QTO) v" + C3DVersions.Qto;
            Width = 560;
            Height = 600;
            MinWidth = 440;
            MinHeight = 420;
            FontFamily = new WM.FontFamily("Arial");
            FontSize = 12;
            Background = T.Back;
            Foreground = T.Text;
            ShowInTaskbar = false;
            WindowStartupLocation = SW.WindowStartupLocation.CenterScreen;

            Content = Build();
        }

        public static void ShowPanel()
        {
            if (_instance is null)
            {
                _instance = new QtoPanel();
                _instance.Closed += (_, _) =>
                {
                    AcApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
                    _instance = null;
                };
                AcApp.DocumentManager.DocumentActivated += OnDocumentActivated;
                AcApp.ShowModelessWindow(_instance);
            }
            else
            {
                _instance.Show();
                _instance.Activate();
            }

            _instance.Reler();
        }

        private static void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
        {
            QtoPanel? p = _instance;
            p?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_instance == p)
                    p.Reler();
            }), SW.Threading.DispatcherPriority.Background);
        }

        // ------------------------------------------------------------------
        // Montagem
        // ------------------------------------------------------------------

        private SW.UIElement Build()
        {
            var root = new WC.DockPanel { Margin = new SW.Thickness(10) };

            // Topo: desenho + reler.
            var topo = new WC.DockPanel { Margin = new SW.Thickness(0, 0, 0, 6) };
            var reler = Botao("Reler corredores", "Le de novo os corredores do desenho atual (nao altera nada).");
            reler.Click += (_, _) => Reler();
            WC.DockPanel.SetDock(reler, WC.Dock.Right);
            topo.Children.Add(reler);
            _desenho.VerticalAlignment = SW.VerticalAlignment.Center;
            _desenho.TextTrimming = SW.TextTrimming.CharacterEllipsis;
            _desenho.FontWeight = SW.FontWeights.Bold;
            topo.Children.Add(_desenho);
            WC.DockPanel.SetDock(topo, WC.Dock.Top);
            root.Children.Add(topo);

            // Rodape: botoes de gerar.
            var rodape = new WC.StackPanel { Margin = new SW.Thickness(0, 8, 0, 0) };

            _aviso.Foreground = T.Muted;
            _aviso.FontSize = 11;
            _aviso.TextWrapping = SW.TextWrapping.Wrap;
            _aviso.Margin = new SW.Thickness(0, 0, 0, 6);
            rodape.Children.Add(_aviso);

            var botoes = new WC.WrapPanel();
            var porEstaca = Botao(
                "Gerar por estaca (marcados)",
                "Memoria de calculo trecho a trecho de cada corredor marcado, no eixo escolhido. " +
                "Gera uma pasta por corredor ao lado do DWG. Esc cancela.");
            porEstaca.FontWeight = SW.FontWeights.Bold;
            porEstaca.Click += (_, _) => GerarPorEstaca();
            botoes.Children.Add(porEstaca);

            var todos = Botao(
                "Total de todos os corredores",
                "Um relatorio so do Civil para o desenho inteiro, separado por corredor, " +
                "com conferencia contra o total do proprio Civil. Rapido (nao divide por estaca).");
            todos.Click += (_, _) => GerarTodos();
            botoes.Children.Add(todos);

            var ajuda = Botao("Ajuda", "Mostra a ajuda do QTO na linha de comando.");
            ajuda.Click += (_, _) => Enviar(C3DCommands.Qto.Help);
            botoes.Children.Add(ajuda);
            rodape.Children.Add(botoes);

            WC.DockPanel.SetDock(rodape, WC.Dock.Bottom);
            root.Children.Add(rodape);

            // Opcoes.
            WC.DockPanel.SetDock(BuildOpcoes(root), WC.Dock.Bottom);

            // Cabecalho da lista: marcar / desmarcar.
            var cab = new WC.DockPanel { Margin = new SW.Thickness(0, 0, 0, 4) };
            var nenhum = Botao("Desmarcar todos", null);
            nenhum.Click += (_, _) => Marcar(false);
            WC.DockPanel.SetDock(nenhum, WC.Dock.Right);
            cab.Children.Add(nenhum);
            var tudo = Botao("Marcar todos", null);
            tudo.Click += (_, _) => Marcar(true);
            WC.DockPanel.SetDock(tudo, WC.Dock.Right);
            cab.Children.Add(tudo);
            cab.Children.Add(new WC.TextBlock
            {
                Text = "Corredor  /  eixo das estacas",
                Foreground = T.Muted,
                VerticalAlignment = SW.VerticalAlignment.Center
            });
            WC.DockPanel.SetDock(cab, WC.Dock.Top);
            root.Children.Add(cab);

            // Lista de corredores.
            var rolagem = new WC.ScrollViewer
            {
                VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled,
                Content = _lista
            };
            root.Children.Add(new WC.Border
            {
                BorderBrush = T.Line,
                BorderThickness = new SW.Thickness(1),
                Background = T.Pane,
                Child = rolagem
            });

            return root;
        }

        private SW.UIElement BuildOpcoes(WC.DockPanel root)
        {
            QtoConfig cfg = QtoConfig.Atual;
            CultureInfo ptBr = CultureInfo.GetCultureInfo("pt-BR");

            var grade = new WC.Grid { Margin = new SW.Thickness(0, 8, 0, 0) };
            grade.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
            grade.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(70) });
            grade.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
            for (int i = 0; i < 3; i++)
                grade.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });

            void Campo(int linha, string rotulo, WC.TextBox caixa, string valor, string explicacao)
            {
                var r = new WC.TextBlock
                {
                    Text = rotulo,
                    VerticalAlignment = SW.VerticalAlignment.Center,
                    Margin = new SW.Thickness(0, 3, 8, 3)
                };
                WC.Grid.SetRow(r, linha);
                grade.Children.Add(r);

                caixa.Text = valor;
                caixa.Height = 22;
                caixa.VerticalContentAlignment = SW.VerticalAlignment.Center;
                caixa.Margin = new SW.Thickness(0, 3, 0, 3);
                caixa.ToolTip = explicacao;
                WC.Grid.SetRow(caixa, linha);
                WC.Grid.SetColumn(caixa, 1);
                grade.Children.Add(caixa);

                var e = new WC.TextBlock
                {
                    Text = explicacao,
                    Foreground = T.Muted,
                    FontSize = 11,
                    TextWrapping = SW.TextWrapping.Wrap,
                    VerticalAlignment = SW.VerticalAlignment.Center,
                    Margin = new SW.Thickness(8, 3, 0, 3)
                };
                WC.Grid.SetRow(e, linha);
                WC.Grid.SetColumn(e, 2);
                grade.Children.Add(e);
            }

            Campo(0, "Intervalo (m)", _intervalo, cfg.Intervalo.ToString("0.###", ptBr),
                "Tamanho de cada trecho da memoria por estaca (padrao 20).");
            Campo(1, "Tolerancia (%)", _tolerancia, cfg.Tolerancia.ToString("0.###", ptBr),
                "Diferenca maxima entre a soma dos trechos e o total do Civil para sair OK (padrao 5).");

            _abrirPasta.Content = "Abrir a pasta da planilha no fim";
            _abrirPasta.IsChecked = cfg.AbrirPasta;
            _abrirPasta.Margin = new SW.Thickness(0, 5, 0, 0);
            WC.Grid.SetRow(_abrirPasta, 2);
            WC.Grid.SetColumnSpan(_abrirPasta, 3);
            grade.Children.Add(_abrirPasta);

            root.Children.Add(grade);
            return grade;
        }

        private WC.Button Botao(string texto, string? dica)
        {
            return new WC.Button
            {
                Content = texto,
                ToolTip = dica,
                Padding = new SW.Thickness(10, 4, 10, 4),
                Margin = new SW.Thickness(0, 0, 6, 0),
                MinHeight = 24
            };
        }

        // ------------------------------------------------------------------
        // Dados
        // ------------------------------------------------------------------

        /// <summary>Le os corredores do desenho atual (so leitura) e refaz a lista.</summary>
        private void Reler()
        {
            HashSet<AcadDb.ObjectId> marcados = _linhas
                .Where(l => l.Marca.IsChecked == true)
                .Select(l => l.Corridor)
                .ToHashSet();
            Dictionary<AcadDb.ObjectId, int> escolhas = _linhas
                .Where(l => l.Escolha is not null)
                .ToDictionary(l => l.Corridor, l => l.Escolha!.SelectedIndex);
            bool mesmoDesenho = ReferenceEquals(_doc, AcApp.DocumentManager.MdiActiveDocument);

            _linhas.Clear();
            _lista.Children.Clear();
            _doc = AcApp.DocumentManager.MdiActiveDocument;

            if (_doc is null)
            {
                _desenho.Text = "Nenhum desenho aberto";
                _aviso.Text = string.Empty;
                return;
            }

            _desenho.Text = Path.GetFileName(_doc.Name);

            try
            {
                using (_doc.LockDocument())
                using (AcadDb.Transaction tr = _doc.Database.TransactionManager.StartTransaction())
                {
                    CivilDocument civil = CivilDocument.GetCivilDocument(_doc.Database);

                    foreach (AcadDb.ObjectId id in civil.CorridorCollection)
                    {
                        if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not CivilDb.Corridor corredor)
                            continue;

                        var linha = new Linha { Corridor = id, Nome = corredor.Name };

                        foreach (CivilDb.Baseline baseline in corredor.Baselines)
                        {
                            try
                            {
                                AcadDb.ObjectId alId = baseline.AlignmentId;
                                if (alId.IsNull || linha.Eixos.Contains(alId))
                                    continue;

                                if (tr.GetObject(alId, AcadDb.OpenMode.ForRead) is CivilDb.Alignment al)
                                {
                                    linha.Eixos.Add(alId);
                                    linha.NomesEixos.Add(al.Name);
                                }
                            }
                            catch
                            {
                                // Baseline por Feature Line: nao tem Alignment.
                            }
                        }

                        _linhas.Add(linha);
                    }

                    tr.Commit();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Erro("QTO", "Painel: falha ao ler os corredores", ex);
                _aviso.Text = "Nao foi possivel ler os corredores deste desenho (detalhe no log, Ctrl+Shift+L).";
                return;
            }

            _linhas.Sort((a, b) => string.Compare(a.Nome, b.Nome, StringComparison.CurrentCultureIgnoreCase));

            foreach (Linha linha in _linhas)
            {
                bool marcado = mesmoDesenho ? marcados.Contains(linha.Corridor) : false;
                int escolha = mesmoDesenho && escolhas.TryGetValue(linha.Corridor, out int i) ? i : 0;
                _lista.Children.Add(MontarLinha(linha, marcado, escolha));
            }

            // Um corredor so: ja vem marcado.
            if (_linhas.Count == 1 && _linhas[0].Eixos.Count > 0)
                _linhas[0].Marca.IsChecked = true;

            _aviso.Text = _linhas.Count == 0
                ? "Este desenho nao tem corredores."
                : "So entra o que tem Pay Item no Code Set Style do corredor. Nada e alterado no desenho; " +
                  "as planilhas saem numa pasta QTO_... ao lado do DWG.";
        }

        private SW.UIElement MontarLinha(Linha linha, bool marcado, int escolha)
        {
            var grade = new WC.Grid { Margin = new SW.Thickness(6, 4, 6, 4) };
            grade.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
            grade.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });

            linha.Marca.Content = linha.Nome;
            linha.Marca.IsChecked = marcado;
            linha.Marca.VerticalAlignment = SW.VerticalAlignment.Center;
            linha.Marca.ToolTip = linha.Nome;
            grade.Children.Add(linha.Marca);

            SW.FrameworkElement eixo;
            if (linha.Eixos.Count == 0)
            {
                linha.Marca.IsEnabled = false;
                linha.Marca.IsChecked = false;
                eixo = new WC.TextBlock
                {
                    Text = "sem Alignment (so entra no total de todos)",
                    Foreground = T.Muted,
                    FontSize = 11
                };
            }
            else if (linha.Eixos.Count == 1)
            {
                eixo = new WC.TextBlock
                {
                    Text = linha.NomesEixos[0],
                    TextTrimming = SW.TextTrimming.CharacterEllipsis,
                    ToolTip = linha.NomesEixos[0]
                };
            }
            else
            {
                var combo = new WC.ComboBox
                {
                    ToolTip = "Este corredor tem " + linha.Eixos.Count +
                              " eixos. A memoria por estaca conta as estacas em um deles."
                };
                foreach (string nome in linha.NomesEixos)
                    combo.Items.Add(nome);
                combo.SelectedIndex = escolha >= 0 && escolha < linha.Eixos.Count ? escolha : 0;
                linha.Escolha = combo;
                eixo = combo;
            }

            eixo.VerticalAlignment = SW.VerticalAlignment.Center;
            eixo.Margin = new SW.Thickness(8, 0, 0, 0);
            WC.Grid.SetColumn(eixo, 1);
            grade.Children.Add(eixo);

            return new WC.Border
            {
                BorderBrush = T.Line,
                BorderThickness = new SW.Thickness(0, 0, 0, 1),
                Child = grade
            };
        }

        private void Marcar(bool valor)
        {
            foreach (Linha linha in _linhas.Where(l => l.Marca.IsEnabled))
                linha.Marca.IsChecked = valor;
        }

        // ------------------------------------------------------------------
        // Acoes
        // ------------------------------------------------------------------

        /// <summary>Confere e grava as opcoes. Devolve false (com aviso) se algum valor nao serve.</summary>
        private bool GuardarOpcoes()
        {
            if (!QtoConfig.LerNumero(_intervalo.Text, out double intervalo) || !QtoConfig.IntervaloValido(intervalo))
            {
                Avisar("Intervalo invalido. Use um numero de 0,5 a 10000 (metros).");
                _intervalo.Focus();
                return false;
            }

            if (!QtoConfig.LerNumero(_tolerancia.Text, out double tolerancia) || !QtoConfig.ToleranciaValida(tolerancia))
            {
                Avisar("Tolerancia invalida. Use um numero de 0 a 100 (por cento).");
                _tolerancia.Focus();
                return false;
            }

            QtoConfig cfg = QtoConfig.Atual;
            cfg.Intervalo = intervalo;
            cfg.Tolerancia = tolerancia;
            cfg.AbrirPasta = _abrirPasta.IsChecked == true;
            cfg.Salvar();
            return true;
        }

        private bool DesenhoConfere()
        {
            Document? atual = AcApp.DocumentManager.MdiActiveDocument;
            if (atual is null)
            {
                Avisar("Nenhum desenho aberto.");
                return false;
            }

            if (!ReferenceEquals(atual, _doc))
            {
                Reler();
                Avisar("O desenho ativo mudou. A lista foi relida; confira os corredores marcados e clique de novo.");
                return false;
            }

            return true;
        }

        private void GerarPorEstaca()
        {
            if (!DesenhoConfere() || !GuardarOpcoes())
                return;

            List<Linha> marcadas = _linhas
                .Where(l => l.Marca.IsChecked == true && l.Eixos.Count > 0)
                .ToList();

            if (marcadas.Count == 0)
            {
                Avisar("Marque pelo menos um corredor na lista.");
                return;
            }

            var pedido = new QtoCommands.QtoPedido
            {
                Documento = _doc,
                Intervalo = QtoConfig.Atual.Intervalo
            };

            foreach (Linha linha in marcadas)
            {
                int i = linha.Escolha?.SelectedIndex ?? 0;
                if (i < 0 || i >= linha.Eixos.Count)
                    i = 0;

                pedido.Itens.Add(new QtoCommands.QtoPedidoItem
                {
                    Corridor = linha.Corridor,
                    Alignment = linha.Eixos[i]
                });
            }

            QtoCommands.PedidoDoPainel = pedido;
            Enviar(C3DCommands.Qto.Estudo);
        }

        private void GerarTodos()
        {
            if (!DesenhoConfere() || !GuardarOpcoes())
                return;

            if (_linhas.Count == 0)
            {
                Avisar("Este desenho nao tem corredores.");
                return;
            }

            QtoCommands.TodosPeloPainel = true;
            Enviar(C3DCommands.Qto.Todos);
        }

        private void Avisar(string texto)
        {
            SW.MessageBox.Show(this, texto, "QTO", SW.MessageBoxButton.OK, SW.MessageBoxImage.Information);
        }

        private static void Enviar(string comando)
        {
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc is null)
                return;

            doc.SendStringToExecute("\x03\x03" + comando + " ", true, false, true);
            try
            {
                Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView();
            }
            catch
            {
                // O comando roda mesmo sem foco.
            }
        }
    }
}
