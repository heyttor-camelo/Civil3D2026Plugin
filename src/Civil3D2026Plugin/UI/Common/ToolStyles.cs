using System;

using SW = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.UI.Common;

/// <summary>
/// Estilos WPF comuns as janelas do plugin no tema claro/escuro: botao com hover proprio, caixas de
/// texto, checkboxes e ComboBox com template proprio (o do Windows ignora a cor de fundo e deixa o
/// texto invisivel no tema escuro), inclusive a lista suspensa e os itens.
/// </summary>
internal static class ToolStyles
{
    public static void Aplicar(SW.ResourceDictionary r, ToolTheme t)
    {
        // Botao com modelo proprio (o hover padrao apaga o texto no tema escuro).
        SW.Style button = new(typeof(WC.Button));
        button.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, t.Hover));
        button.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, t.Text));
        button.Setters.Add(new SW.Setter(WC.Control.BorderBrushProperty, t.Line));
        button.Setters.Add(new SW.Setter(WC.Control.BorderThicknessProperty, new SW.Thickness(1)));
        button.Setters.Add(new SW.Setter(WC.Control.HorizontalContentAlignmentProperty, SW.HorizontalAlignment.Center));
        button.Setters.Add(new SW.Setter(WC.Control.VerticalContentAlignmentProperty, SW.VerticalAlignment.Center));
        SW.FrameworkElementFactory bbd = new(typeof(WC.Border), "Bd");
        bbd.SetValue(WC.Border.BackgroundProperty, new SW.TemplateBindingExtension(WC.Control.BackgroundProperty));
        bbd.SetValue(WC.Border.BorderBrushProperty, new SW.TemplateBindingExtension(WC.Control.BorderBrushProperty));
        bbd.SetValue(WC.Border.BorderThicknessProperty, new SW.TemplateBindingExtension(WC.Control.BorderThicknessProperty));
        bbd.SetValue(WC.Border.PaddingProperty, new SW.TemplateBindingExtension(WC.Control.PaddingProperty));
        bbd.SetValue(WC.Border.SnapsToDevicePixelsProperty, true);
        SW.FrameworkElementFactory bcp = new(typeof(WC.ContentPresenter));
        bcp.SetValue(SW.FrameworkElement.HorizontalAlignmentProperty, new SW.TemplateBindingExtension(WC.Control.HorizontalContentAlignmentProperty));
        bcp.SetValue(SW.FrameworkElement.VerticalAlignmentProperty, new SW.TemplateBindingExtension(WC.Control.VerticalContentAlignmentProperty));
        bbd.AppendChild(bcp);
        WC.ControlTemplate buttonTemplate = new(typeof(WC.Button)) { VisualTree = bbd };
        SW.Trigger bHover = new() { Property = SW.UIElement.IsMouseOverProperty, Value = true };
        bHover.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, t.Selected, "Bd"));
        buttonTemplate.Triggers.Add(bHover);
        SW.Trigger bPressed = new() { Property = WC.Primitives.ButtonBase.IsPressedProperty, Value = true };
        bPressed.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, t.Line, "Bd"));
        buttonTemplate.Triggers.Add(bPressed);
        SW.Trigger bDisabled = new() { Property = SW.UIElement.IsEnabledProperty, Value = false };
        bDisabled.Setters.Add(new SW.Setter(SW.UIElement.OpacityProperty, 0.45));
        buttonTemplate.Triggers.Add(bDisabled);
        button.Setters.Add(new SW.Setter(WC.Control.TemplateProperty, buttonTemplate));
        r[typeof(WC.Button)] = button;

        SW.Style check = new(typeof(WC.CheckBox));
        check.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, t.Text));
        r[typeof(WC.CheckBox)] = check;

        SW.Style text = new(typeof(WC.TextBox));
        text.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, t.Input));
        text.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, t.Text));
        text.Setters.Add(new SW.Setter(WC.Control.BorderBrushProperty, t.Line));
        text.Setters.Add(new SW.Setter(WC.TextBox.CaretBrushProperty, t.Text));
        text.Setters.Add(new SW.Setter(WC.Control.PaddingProperty, new SW.Thickness(3, 1, 3, 1)));
        r[typeof(WC.TextBox)] = text;

        SW.Style combo = new(typeof(WC.ComboBox));
        combo.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, t.Text));
        combo.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, t.Input));
        combo.Setters.Add(new SW.Setter(WC.Control.BorderBrushProperty, t.Line));
        combo.Setters.Add(new SW.Setter(WC.Control.MinHeightProperty, 22.0));
        // O ComboBox do tema do Windows (Aero2) ignora Background: caixa clara com texto claro no
        // tema escuro. Template proprio para editavel e nao editavel.
        WC.ControlTemplate? comboTemplate = ComboTemplate(t);
        if (comboTemplate is not null)
            combo.Setters.Add(new SW.Setter(WC.Control.TemplateProperty, comboTemplate));
        r[typeof(WC.ComboBox)] = combo;

        // A lista suspensa do ComboBox usa as cores de sistema "Window": redefinidas aqui para o tema.
        r[SW.SystemColors.WindowBrushKey] = t.Pane;
        r[SW.SystemColors.WindowFrameBrushKey] = t.Line;
        r[SW.SystemColors.WindowTextBrushKey] = t.Text;
        r[SW.SystemColors.HighlightBrushKey] = t.Selected;
        r[SW.SystemColors.HighlightTextBrushKey] = t.Text;
        r[SW.SystemColors.ControlBrushKey] = t.Back;
        r[SW.SystemColors.ControlTextBrushKey] = t.Text;

        SW.Style comboItem = new(typeof(WC.ComboBoxItem));
        comboItem.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, t.Text));
        comboItem.Setters.Add(new SW.Setter(WC.Control.BackgroundProperty, t.Pane));
        comboItem.Setters.Add(new SW.Setter(WC.Control.PaddingProperty, new SW.Thickness(6, 3, 6, 3)));
        SW.FrameworkElementFactory cbd = new(typeof(WC.Border), "Bd");
        cbd.SetValue(WC.Border.BackgroundProperty, new SW.TemplateBindingExtension(WC.Control.BackgroundProperty));
        cbd.SetValue(WC.Border.PaddingProperty, new SW.TemplateBindingExtension(WC.Control.PaddingProperty));
        cbd.SetValue(WC.Border.SnapsToDevicePixelsProperty, true);
        cbd.AppendChild(new SW.FrameworkElementFactory(typeof(WC.ContentPresenter)));
        WC.ControlTemplate comboItemTemplate = new(typeof(WC.ComboBoxItem)) { VisualTree = cbd };
        SW.Trigger ciHover = new() { Property = SW.UIElement.IsMouseOverProperty, Value = true };
        ciHover.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, t.Hover, "Bd"));
        comboItemTemplate.Triggers.Add(ciHover);
        SW.Trigger ciSel = new() { Property = WC.ComboBoxItem.IsSelectedProperty, Value = true };
        ciSel.Setters.Add(new SW.Setter(WC.Border.BackgroundProperty, t.Selected, "Bd"));
        comboItemTemplate.Triggers.Add(ciSel);
        comboItem.Setters.Add(new SW.Setter(WC.Control.TemplateProperty, comboItemTemplate));
        r[typeof(WC.ComboBoxItem)] = comboItem;

        SW.Style radio = new(typeof(WC.RadioButton));
        radio.Setters.Add(new SW.Setter(WC.Control.ForegroundProperty, t.Text));
        r[typeof(WC.RadioButton)] = radio;
    }

    private static string Hex(WM.Brush b) =>
        b is WM.SolidColorBrush sc ? $"#{sc.Color.A:X2}{sc.Color.R:X2}{sc.Color.G:X2}{sc.Color.B:X2}" : "#FF808080";

    private static WC.ControlTemplate? ComboTemplate(ToolTheme t)
    {
        string xaml = @"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBox'>
  <Grid SnapsToDevicePixels='True'>
    <ToggleButton x:Name='Toggle' Focusable='False' ClickMode='Press'
                  IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
      <ToggleButton.Template>
        <ControlTemplate TargetType='ToggleButton'>
          <Border x:Name='Bd' Background='{INPUT}' BorderBrush='{LINE}' BorderThickness='1'>
            <Path HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,7,0'
                  Data='M 0 0 L 4 4 L 8 0' Stroke='{TEXT}' StrokeThickness='1.4'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='BorderBrush' Value='{ACCENT}'/>
            </Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Bd' Property='BorderBrush' Value='{ACCENT}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </ToggleButton.Template>
    </ToggleButton>
    <ContentPresenter x:Name='Conteudo' IsHitTestVisible='False' Margin='6,2,24,2'
                      VerticalAlignment='Center' HorizontalAlignment='Left'
                      Content='{TemplateBinding SelectionBoxItem}'
                      ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                      ContentTemplateSelector='{TemplateBinding ItemTemplateSelector}'
                      TextElement.Foreground='{TEXT}'/>
    <TextBox x:Name='PART_EditableTextBox' Visibility='Hidden' Margin='1,1,22,1' Padding='4,1,2,1'
             Background='{INPUT}' Foreground='{TEXT}' CaretBrush='{TEXT}' BorderThickness='0'
             VerticalContentAlignment='Center' IsReadOnly='{TemplateBinding IsReadOnly}'/>
    <Popup x:Name='PART_Popup' Placement='Bottom' AllowsTransparency='True' Focusable='False'
           PopupAnimation='None' IsOpen='{TemplateBinding IsDropDownOpen}'>
      <Border Background='{PANE}' BorderBrush='{LINE}' BorderThickness='1'
              MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'
              MaxHeight='{TemplateBinding MaxDropDownHeight}'>
        <ScrollViewer SnapsToDevicePixels='True' CanContentScroll='True'>
          <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/>
        </ScrollViewer>
      </Border>
    </Popup>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property='IsEditable' Value='True'>
      <Setter TargetName='PART_EditableTextBox' Property='Visibility' Value='Visible'/>
      <Setter TargetName='Conteudo' Property='Visibility' Value='Hidden'/>
    </Trigger>
    <Trigger Property='IsEnabled' Value='False'>
      <Setter Property='Opacity' Value='0.5'/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>"
            .Replace("{INPUT}", Hex(t.Input)).Replace("{LINE}", Hex(t.Line)).Replace("{TEXT}", Hex(t.Text))
            .Replace("{ACCENT}", Hex(t.Accent)).Replace("{PANE}", Hex(t.Pane));
        try
        {
            return (WC.ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        }
        catch (System.Exception)
        {
            // Se o XAML falhar, fica o template do Windows (com as cores de sistema ja trocadas).
            return null;
        }
    }

}
