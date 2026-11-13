// SettingsUi: the settings window. A live preview on the left (the same glass the banners use, three cards you can
// drag around a few backdrops), the settings on the right in three tabs: glass, shadows, sound.
// Every change is applied at once and saved to settings.ini.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GlassBell
{
    sealed class SettingsUi : Window
    {
        const double SW = 520, SH = 580;                      // size of the preview scene in DIPs

        static readonly string[][] Swatches =
        {
            new[] { L.T("color.auto"), "auto" }, new[] { L.T("color.1"), "#1C1C1E" }, new[] { L.T("color.2"), "#000000" },
            new[] { L.T("color.3"), "#FFFFFF" }, new[] { L.T("color.4"), "#0A84FF" }, new[] { L.T("color.5"), "#5E5CE6" },
            new[] { L.T("color.6"), "#30B050" }, new[] { L.T("color.7"), "#FF9F0A" }, new[] { L.T("color.8"), "#FF453A" },
            new[] { L.T("color.9"), "#FF375F" }
        };
        static readonly string[] Scenes = { L.T("scene.0"), L.T("scene.1"), L.T("scene.2"), L.T("scene.3") };
        static readonly string[] Qualities = { L.T("quality.0"), L.T("quality.1"), L.T("quality.2"), L.T("quality.3") };
        static readonly string[] QualityNotes =
        {
            L.T("quality.0.note"),
            L.T("quality.1.note"),
            L.T("quality.2.note"),
            L.T("quality.3.note")
        };
        static readonly string[] SoundNotes = { L.T("chime.0.note"), L.T("chime.1.note"), L.T("chime.2.note") };

        // the look of every control: slim sliders, switches, segmented controls, dark buttons and scroll bars
        const string Styles = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>

  <Style TargetType='{x:Type Slider}'>
    <Setter Property='Focusable' Value='True'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Slider}'>
          <Grid Height='24' Background='Transparent' SnapsToDevicePixels='True'>
            <Border Height='4' CornerRadius='2' Background='#48484A' VerticalAlignment='Center'/>
            <Track x:Name='PART_Track'>
              <Track.DecreaseRepeatButton>
                <RepeatButton Command='Slider.DecreaseLarge' Focusable='False' IsTabStop='False'>
                  <RepeatButton.Template>
                    <ControlTemplate TargetType='{x:Type RepeatButton}'>
                      <Border Background='Transparent'>
                        <Border Height='4' CornerRadius='2' Background='#0A84FF' VerticalAlignment='Center'/>
                      </Border>
                    </ControlTemplate>
                  </RepeatButton.Template>
                </RepeatButton>
              </Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton>
                <RepeatButton Command='Slider.IncreaseLarge' Focusable='False' IsTabStop='False'>
                  <RepeatButton.Template>
                    <ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate>
                  </RepeatButton.Template>
                </RepeatButton>
              </Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb Width='18' Height='18' Focusable='False'>
                  <Thumb.Template>
                    <ControlTemplate TargetType='{x:Type Thumb}'>
                      <Grid>
                        <Ellipse x:Name='Ring' Margin='-3' Stroke='#660A84FF' StrokeThickness='3' Opacity='0'/>
                        <Ellipse x:Name='Knob' Fill='White' Stroke='#40000000' StrokeThickness='1'/>
                      </Grid>
                      <ControlTemplate.Triggers>
                        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Knob' Property='Fill' Value='#F2F2F7'/></Trigger>
                        <Trigger Property='IsDragging' Value='True'><Setter TargetName='Ring' Property='Opacity' Value='1'/></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.35'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Switch' TargetType='{x:Type CheckBox}'>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type CheckBox}'>
          <Border x:Name='Track' Width='42' Height='26' CornerRadius='13' Background='#48484A' BorderThickness='2' BorderBrush='Transparent' SnapsToDevicePixels='True'>
            <Ellipse x:Name='Knob' Width='20' Height='20' Fill='White' HorizontalAlignment='Left' Margin='1,0,0,0'>
              <Ellipse.RenderTransform><TranslateTransform X='0'/></Ellipse.RenderTransform>
            </Ellipse>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Track' Property='Background' Value='#0A84FF'/>
              <Trigger.EnterActions>
                <BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='(UIElement.RenderTransform).(TranslateTransform.X)' To='16' Duration='0:0:0.14'/></Storyboard></BeginStoryboard>
              </Trigger.EnterActions>
              <Trigger.ExitActions>
                <BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='(UIElement.RenderTransform).(TranslateTransform.X)' To='0' Duration='0:0:0.14'/></Storyboard></BeginStoryboard>
              </Trigger.ExitActions>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Track' Property='BorderBrush' Value='#990A84FF'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Seg' TargetType='{x:Type RadioButton}'>
    <Setter Property='Foreground' Value='#D1D1D6'/>
    <Setter Property='FontSize' Value='12.5'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type RadioButton}'>
          <Border x:Name='B' CornerRadius='7' Padding='6,6' Background='Transparent' BorderThickness='1.5' BorderBrush='Transparent' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' TextElement.Foreground='{TemplateBinding Foreground}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#3A3A3C'/></Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='B' Property='Background' Value='#0A84FF'/>
              <Setter Property='Foreground' Value='White'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#99FFFFFF'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Tab' TargetType='{x:Type RadioButton}' BasedOn='{StaticResource Seg}'>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type RadioButton}'>
          <Border x:Name='B' CornerRadius='8' Padding='6,7' Background='Transparent' BorderThickness='1.5' BorderBrush='Transparent' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' TextElement.Foreground='{TemplateBinding Foreground}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#2C2C2E'/></Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='B' Property='Background' Value='#48484A'/>
              <Setter Property='Foreground' Value='White'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#99FFFFFF'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Chip' TargetType='{x:Type RadioButton}' BasedOn='{StaticResource Seg}'>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Margin' Value='0,2,5,2'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type RadioButton}'>
          <Border x:Name='B' CornerRadius='13' Padding='9,4' Background='#2C2C2E' BorderThickness='1.5' BorderBrush='Transparent' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' TextElement.Foreground='{TemplateBinding Foreground}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#3A3A3C'/></Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='B' Property='Background' Value='#48484A'/>
              <Setter Property='Foreground' Value='White'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#99FFFFFF'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Melody' TargetType='{x:Type RadioButton}'>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type RadioButton}'>
          <Border x:Name='B' CornerRadius='10' Padding='12,9' Background='#2C2C2E' BorderThickness='1.5' BorderBrush='Transparent' SnapsToDevicePixels='True'>
            <StackPanel Orientation='Horizontal'>
              <Grid Width='18' Height='18' VerticalAlignment='Center'>
                <Ellipse Stroke='#8E8E93' StrokeThickness='1.5'/>
                <Ellipse x:Name='Dot' Width='10' Height='10' Fill='#0A84FF' Visibility='Collapsed'/>
              </Grid>
              <ContentPresenter Margin='12,0,0,0' VerticalAlignment='Center'/>
            </StackPanel>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#3A3A3C'/></Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='B' Property='BorderBrush' Value='#0A84FF'/>
              <Setter TargetName='Dot' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#99FFFFFF'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='{x:Type Button}'>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Button}'>
          <Border x:Name='B' CornerRadius='9' Padding='16,8' Background='#3A3A3C' BorderThickness='1.5' BorderBrush='Transparent' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#48484A'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='B' Property='Background' Value='#2C2C2E'/></Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#99FFFFFF'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Primary' TargetType='{x:Type Button}' BasedOn='{StaticResource {x:Type Button}}'>
    <Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Button}'>
          <Border x:Name='B' CornerRadius='9' Padding='18,8' Background='#0A84FF' BorderThickness='1.5' BorderBrush='Transparent' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#3395FF'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='B' Property='Background' Value='#0066CC'/></Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#99FFFFFF'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Link' TargetType='{x:Type Button}' BasedOn='{StaticResource {x:Type Button}}'>
    <Setter Property='Foreground' Value='#8E8E93'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Button}'>
          <Border x:Name='B' CornerRadius='6' Padding='6,3' Background='Transparent' SnapsToDevicePixels='True'>
            <ContentPresenter x:Name='C' HorizontalAlignment='Center' VerticalAlignment='Center' TextElement.Foreground='{TemplateBinding Foreground}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='B' Property='Background' Value='#2C2C2E'/>
              <Setter Property='Foreground' Value='White'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Play' TargetType='{x:Type Button}' BasedOn='{StaticResource {x:Type Button}}'>
    <Setter Property='Width' Value='30'/>
    <Setter Property='Height' Value='30'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Button}'>
          <Grid SnapsToDevicePixels='True'>
            <Ellipse x:Name='E' Fill='#3A3A3C'/>
            <Path x:Name='P' Data='M 0,0 L 9,5.5 L 0,11 Z' Fill='White' Stretch='None' HorizontalAlignment='Center' VerticalAlignment='Center' Margin='3,0,0,0'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='E' Property='Fill' Value='#0A84FF'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='E' Property='Fill' Value='#0066CC'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='{x:Type TextBox}'>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='CaretBrush' Value='White'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type TextBox}'>
          <Border x:Name='B' CornerRadius='8' Padding='9,6' Background='#1C1C1E' BorderThickness='1.5' BorderBrush='#48484A'>
            <ScrollViewer x:Name='PART_ContentHost' Focusable='False'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#0A84FF'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='{x:Type ScrollBar}'>
    <Setter Property='Width' Value='8'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type ScrollBar}'>
          <Grid Background='Transparent'>
            <Track x:Name='PART_Track' IsDirectionReversed='True'>
              <Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/></Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/></Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb>
                  <Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'><Border CornerRadius='4' Background='#636366' Margin='1,0'/></ControlTemplate></Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Combo' TargetType='{x:Type ComboBox}'>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type ComboBox}'>
          <Grid>
            <ToggleButton Focusable='False' ClickMode='Press' IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
              <ToggleButton.Template>
                <ControlTemplate TargetType='{x:Type ToggleButton}'>
                  <Border x:Name='B' CornerRadius='7' Background='#2C2C2E' BorderBrush='#3A3A3C' BorderThickness='1' SnapsToDevicePixels='True'/>
                  <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#3A3A3C'/></Trigger>
                    <Trigger Property='IsChecked' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#0A84FF'/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter IsHitTestVisible='False' Content='{TemplateBinding SelectionBoxItem}' Margin='9,4,26,4' VerticalAlignment='Center' HorizontalAlignment='Left'/>
            <Path IsHitTestVisible='False' FlowDirection='LeftToRight' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,1,10,0'
                  Data='M 0 0 L 4 4 L 8 0' Stroke='#AEAEB2' StrokeThickness='1.5' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
            <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Top' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
              <Border Background='#2C2C2E' BorderBrush='#48484A' BorderThickness='1' CornerRadius='8' Padding='4' Margin='0,0,0,4'
                      MinWidth='{TemplateBinding ActualWidth}' MaxHeight='{TemplateBinding MaxDropDownHeight}'>
                <ScrollViewer VerticalScrollBarVisibility='Auto'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='{x:Type ComboBoxItem}'>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='FontSize' Value='12.5'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type ComboBoxItem}'>
          <Border x:Name='B' CornerRadius='5' Padding='9,4' Background='Transparent' SnapsToDevicePixels='True'>
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='B' Property='Background' Value='#3A3A3C'/></Trigger>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#3A3A3C'/></Trigger>
            <Trigger Property='IsSelected' Value='True'><Setter TargetName='B' Property='Background' Value='#0A84FF'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='{x:Type ToolTip}'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type ToolTip}'>
          <Border CornerRadius='7' Background='#1C1C1E' BorderBrush='#48484A' BorderThickness='1' Padding='9,5'>
            <ContentPresenter TextElement.Foreground='#E5E5EA' TextElement.FontSize='12'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

        // what a slider row needs to know about itself
        sealed class SliderRow
        {
            public Slider Slider;
            public TextBlock Value, Hint;
            public string HintOn, HintOff;
            public bool GlassOnly;
            public Func<int> Get;
        }

        readonly Manager mgr;
        readonly Canvas canvas = new Canvas { Width = SW, Height = SH, ClipToBounds = true };
        readonly Image sceneImage = new Image { Width = SW, Height = SH, IsHitTestVisible = false };
        readonly List<GlassCard> cards = new List<GlassCard>();
        readonly List<SliderRow> rows = new List<SliderRow>();
        readonly List<RadioButton> qualityButtons = new List<RadioButton>(), sceneButtons = new List<RadioButton>(), melodyButtons = new List<RadioButton>();
        readonly List<Grid> swatchViews = new List<Grid>();
        readonly List<UIElement> soundDependents = new List<UIElement>();
        readonly List<FrameworkElement> tabPanels = new List<FrameworkElement>();
        readonly CheckBox master = new CheckBox { Style = null };
        readonly CheckBox soundOn = new CheckBox(), soundAdapt = new CheckBox(), liveSwitch = new CheckBox();
        readonly Grid liveRow = new Grid();
        readonly StackPanel liveFpsRow = new StackPanel();
        readonly List<RadioButton> fpsButtons = new List<RadioButton>(), engineButtons = new List<RadioButton>();
        readonly TextBlock qualityNote = new TextBlock(), timing = new TextBlock(), engineNote = new TextBlock(), fpsHint = new TextBlock();
        TextBlock colorName;   // created by Section(), which hands it out
        readonly TextBox hex = new TextBox();
        readonly Border hexChip = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), BorderBrush = Hue("#48484A") };
        readonly DispatcherTimer load = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        readonly Process self = Process.GetCurrentProcess();
        TimeSpan lastCpu;
        DateTime lastCpuAt;
        double lastBakeMs = -1;
        byte[] sceneBuf;
        int sceneW, sceneH, tab;
        double scale;
        bool loading = true;
        GlassCard dragging;
        Point dragStart, dragOrigin;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        FrameworkElement leftColumn, chipsPanel;
        bool sizeLocked;
        readonly List<RadioButton> tabButtons = new List<RadioButton>();

        public int Tab { get { return tab; } }

        public void SelectTab(int index) { if (index >= 0 && index < tabButtons.Count) tabButtons[index].IsChecked = true; }

        // An entry the pointer does not focus. A ComboBox focuses the entry the pointer enters, and a focused entry is
        // scrolled fully into view, so resting on the half-visible bottom entry scrolled the list, then the next one did,
        // and so on. The hover colour comes from IsMouseOver in the style; clicks, the wheel, the scroll bar, the arrow
        // keys and showing the picked language when the list opens work as before.
        sealed class ListEntry : ComboBoxItem
        {
            protected override void OnMouseEnter(MouseEventArgs e) { }
            protected override void OnMouseMove(MouseEventArgs e) { }
        }

        // the interface language: "as in Windows" or one of the languages, each written in itself
        FrameworkElement BuildLanguagePicker()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ToolTip = L.T("settings.language") };
            row.Children.Add(new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,   // a globe
                Foreground = Hue("#8E8E93"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0)
            });
            var box = new ComboBox { Style = (Style)FindResource("Combo"), MinWidth = 120, MaxDropDownHeight = 330 };
            AutomationProperties.SetName(box, L.T("settings.language"));
            AutomationProperties.SetAutomationId(box, "language");
            var entryStyle = (Style)FindResource(typeof(ComboBoxItem));
            box.Items.Add(new ListEntry { Content = L.T("settings.languageAuto"), Tag = "auto", Style = entryStyle });
            for (int i = 0; i < L.Codes.Length; i++)
                box.Items.Add(new ListEntry
                {
                    Content = L.Names[i], Tag = L.Codes[i], Style = entryStyle,
                    Language = XmlLanguage.GetLanguage(L.Codes[i] == "zh" ? "zh-Hans" : L.Codes[i]),   // each name in the glyphs of its own language
                    FlowDirection = L.Codes[i] == "ar" || L.Codes[i] == "ur" || L.Codes[i] == "fa" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
                });
            string current = (Settings.Language ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(L.Codes, current) < 0) current = "auto";
            foreach (ComboBoxItem it in box.Items) if ((string)it.Tag == current) box.SelectedItem = it;
            box.SelectionChanged += delegate
            {
                var it = box.SelectedItem as ComboBoxItem;
                if (it == null || (string)it.Tag == current) return;
                string code = (string)it.Tag;
                // once the drop-down has closed: the window is built again in the new language
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { mgr.ChangeLanguage(code); }));
            };
            row.Children.Add(box);
            return row;
        }

        // the title bar follows the dark window instead of staying white; there is no maximize button, as a maximized
        // window could only add empty space around content of fixed size
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                int on = 1;
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                DwmSetWindowAttribute(h, 20, ref on, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
                SetWindowLong(h, -16, GetWindowLong(h, -16) & ~0x10000);   // GWL_STYLE without WS_MAXIMIZEBOX
            }
            catch { }
        }

        // once laid out: the width stays that of the content, the height may only shrink, down to the preview
        void LockSize()
        {
            if (sizeLocked) return;
            sizeLocked = true;
            var root = (FrameworkElement)Content;
            double w = ActualWidth, h = ActualHeight;
            double chrome = h - root.ActualHeight - root.Margin.Top - root.Margin.Bottom;   // title bar and frame
            SizeToContent = SizeToContent.Manual;
            Width = w;
            Height = h;
            MinWidth = MaxWidth = w;
            MaxHeight = h;
            MinHeight = Math.Min(h, leftColumn.ActualHeight + root.Margin.Top + root.Margin.Bottom + chrome);
        }

        static Brush Hue(string hex)
        {
            var b = (Brush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        public SettingsUi(Manager mgr)
        {
            this.mgr = mgr;
            Settings.Load();
            scale = Manager.Scale();
            Title = L.T("settings.title");
            // The preview is a picture of fixed size and the panel a column of fixed width, so nothing in the window can
            // use more width: the window takes the width of its content and keeps it. The height starts at what all the
            // settings need (at most the screen) and can only shrink, while the settings scroll, down to the height of the
            // preview. Both limits come from the laid-out content (see LockSize), so every language gets its own.
            SizeToContent = SizeToContent.WidthAndHeight;
            MaxHeight = SystemParameters.WorkArea.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            Language = XmlLanguage.GetLanguage(L.Ietf);   // picks the fallback font for Chinese, Japanese and Korean
            Background = Hue("#1C1C1E");
            Foreground = Brushes.White;
            UseLayoutRounding = true;
            Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Styles));
            master.Style = (Style)FindResource("Switch");

            var root = new Grid { Margin = new Thickness(20) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });

            // ---- left: the preview, with its backdrop chips right under it ----
            var left = new Grid { Margin = new Thickness(0, 0, 20, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            leftColumn = left;
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var sceneHost = new Border
            {
                Width = SW, Height = SH, CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = canvas,
                VerticalAlignment = VerticalAlignment.Top, BorderBrush = Hue("#3A3A3C"), BorderThickness = new Thickness(1)
            };
            canvas.Children.Add(sceneImage);
            canvas.Clip = new RectangleGeometry(new Rect(0, 0, SW, SH), 13, 13);   // Border alone clips to a plain rectangle
            Grid.SetRow(sceneHost, 0);
            left.Children.Add(sceneHost);

            var below = new Grid { Margin = new Thickness(0, 14, 0, 0), Width = SW + 2 };
            if (L.Rtl) below.FlowDirection = FlowDirection.RightToLeft;   // the backdrop chips start from the right
            below.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            below.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // the backdrop chips wrap onto a second row when a language needs more room than one row has
            var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            chipsPanel = chips;
            chips.Children.Add(new TextBlock { Text = L.T("settings.backdrop"), Foreground = Hue("#8E8E93"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            for (int i = 0; i < Scenes.Length; i++)
            {
                int idx = i;
                var chip = new RadioButton { Content = Scenes[i], GroupName = "scene", Style = (Style)FindResource("Chip"), IsChecked = i == Settings.Scene };
                chip.Checked += delegate
                {
                    if (loading) return;
                    Settings.Scene = idx;
                    Settings.Save();
                    SetScene(idx);
                };
                sceneButtons.Add(chip);
                chips.Children.Add(chip);
            }
            var show = new Button { Content = L.T("settings.showBanner"), Style = (Style)FindResource("Primary"), VerticalAlignment = VerticalAlignment.Center };
            show.Click += delegate { mgr.Preview(); };
            Grid.SetColumn(show, 1);
            below.Children.Add(chips);
            below.Children.Add(show);
            Grid.SetRow(below, 1);
            left.Children.Add(below);
            Grid.SetColumn(left, 0);
            root.Children.Add(left);

            // ---- right: the settings ----
            var panel = new Border { Background = Hue("#242426"), CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 18, 20, 12) };
            // Arabic, Urdu, Persian: the settings read right to left; the preview stays as it is, it is a picture
            if (L.Rtl) panel.FlowDirection = FlowDirection.RightToLeft;
            var col = new Grid();
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titles = new StackPanel();
            titles.Children.Add(new TextBlock { Text = L.T("settings.glass"), FontSize = 20, FontWeight = FontWeights.SemiBold });
            titles.Children.Add(new TextBlock { Text = L.T("settings.glassHint"), FontSize = 12, Foreground = Hue("#8E8E93"), Margin = new Thickness(0, 2, 0, 0) });
            head.Children.Add(titles);
            master.IsChecked = Settings.Glass;
            master.VerticalAlignment = VerticalAlignment.Center;
            master.ToolTip = L.T("settings.glassTip");
            AutomationProperties.SetName(master, L.T("settings.glass"));
            AutomationProperties.SetAutomationId(master, "glassSwitch");
            master.Checked += delegate { Changed(); };
            master.Unchecked += delegate { Changed(); };
            Grid.SetColumn(master, 1);
            head.Children.Add(master);
            Grid.SetRow(head, 0);
            col.Children.Add(head);

            // tabs
            var tabs = new UniformGrid { Rows = 1, Margin = new Thickness(0, 16, 0, 0) };
            var tabHost = new Border { Background = Hue("#1C1C1E"), CornerRadius = new CornerRadius(10), Padding = new Thickness(3), Child = tabs, Margin = new Thickness(0, 16, 0, 0) };
            string[] tabNames = { L.T("tab.glass"), L.T("tab.shadow"), L.T("tab.sound") };
            for (int i = 0; i < tabNames.Length; i++)
            {
                int idx = i;
                var rb = new RadioButton { Content = tabNames[i], GroupName = "tab", Style = (Style)FindResource("Tab"), IsChecked = i == 0, Margin = new Thickness(i > 0 ? 2 : 0, 0, 0, 0) };
                rb.Checked += delegate { ShowTab(idx); };
                AutomationProperties.SetAutomationId(rb, "tab" + i);
                tabButtons.Add(rb);
                tabs.Children.Add(rb);
            }
            tabs.Margin = new Thickness(0);
            Grid.SetRow(tabHost, 1);
            col.Children.Add(tabHost);

            var body = new Grid();
            tabPanels.Add(BuildGlassTab());
            tabPanels.Add(BuildShadowTab());
            tabPanels.Add(BuildSoundTab());
            foreach (var t in tabPanels) body.Children.Add(t);
            var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, -6, 0), Padding = new Thickness(0, 0, 6, 0) };
            Grid.SetRow(scroll, 2);
            col.Children.Add(scroll);

            // bottom: the two reset links, and the interface language on the other side
            var foot = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var footRow = new Grid();
            footRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var resets = new WrapPanel { Margin = new Thickness(-6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var resetTab = new Button { Content = L.T("settings.resetTab"), Style = (Style)FindResource("Link") };
            resetTab.Click += delegate { ResetTab(); };
            var resetAll = new Button { Content = L.T("settings.resetAll"), Style = (Style)FindResource("Link") };
            resetAll.Click += delegate { ResetAll(); };
            resets.Children.Add(resetTab);
            resets.Children.Add(resetAll);
            timing.FontSize = 11;
            timing.Foreground = Hue("#636366");
            timing.Margin = new Thickness(0, 2, 0, 0);
            timing.TextTrimming = TextTrimming.CharacterEllipsis;
            AutomationProperties.SetAutomationId(timing, "timing");
            footRow.Children.Add(resets);
            var language = BuildLanguagePicker();
            Grid.SetColumn(language, 1);
            footRow.Children.Add(language);
            foot.Children.Add(footRow);
            foot.Children.Add(timing);
            Grid.SetRow(foot, 3);
            col.Children.Add(foot);

            panel.Child = col;
            Grid.SetColumn(panel, 1);
            root.Children.Add(panel);
            Content = root;

            var look = Look.FromSettings();
            AddCard(new GlassCard(look, 340, 0, false, "GlassBell", L.T("preview.title"),
                L.T("card.1"), null), 24, 44);
            AddCard(new GlassCard(look, 290, 160, false, "", L.T("card.2.title"),
                L.T("card.2"), null), 190, 240);
            AddCard(new GlassCard(look, 160, 54, true, "", L.T("card.3.title"), "", null), 40, 470);

            SetScene(Settings.Scene);
            ShowTab(0);
            loading = false;
            Refresh();
            load.Tick += delegate { ShowTiming(); };
            Loaded += delegate { RebakeAll(); load.Start(); };
            ContentRendered += delegate { LockSize(); };
            Closed += delegate { load.Stop(); foreach (var c in cards) c.ReleaseEffects(); };
            if (SnapshotPath != null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -20000;
                Top = 0;
                ShowActivated = false;
                ShowInTaskbar = false;
                Loaded += delegate { Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(SaveSnapshots)); };
            }
        }

        // GLASSBELL_SNAPSHOT=file.png (a test setting, for a test instance): the window opens off-screen, saves a
        // picture of each tab (file-0.png, file-1.png, file-2.png) and the program quits. This is how every
        // translation is checked for text that does not fit.
        public static readonly string SnapshotPath = Environment.GetEnvironmentVariable("GLASSBELL_SNAPSHOT");

        void SaveSnapshots()
        {
            try
            {
                UpdateLayout();
                LockSize();
                Log.Write("snapshot " + L.Code + ": " + Math.Round(ActualWidth) + " x " + Math.Round(ActualHeight) + ", height down to " + Math.Round(MinHeight) +
                    ", screen " + Math.Round(SystemParameters.WorkArea.Height) + (chipsPanel.ActualHeight > 40 ? ", backdrop chips wrap" : ""));
                var root = (FrameworkElement)Content;
                // tabs 0..2 at the full height, then tab 0 at the smallest height the window allows (file-3.png)
                for (int t = 0; t <= tabPanels.Count; t++)
                {
                    ShowTab(t % tabPanels.Count);
                    if (t == tabPanels.Count) Height = MinHeight;
                    UpdateLayout();
                    double w = root.ActualWidth + 40, h = root.ActualHeight + 40;
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
                        dc.DrawRectangle(new VisualBrush(root), null, new Rect(20, 20, root.ActualWidth, root.ActualHeight));
                    }
                    var bmp = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(dv);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    string file = System.IO.Path.ChangeExtension(SnapshotPath, null) + "-" + t + ".png";
                    using (var f = System.IO.File.Create(file)) enc.Save(f);
                    ReportOverflow(t);
                }
            }
            catch (Exception e) { Log.Write("snapshot: " + e.Message); }
            Application.Current.Shutdown();
        }

        // writes to the log every visible text that needs more room than it has (with SnapshotPath)
        void ReportOverflow(int tab)
        {
            var todo = new Stack<DependencyObject>();
            todo.Push(this);
            while (todo.Count > 0)
            {
                var d = todo.Pop();
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) todo.Push(VisualTreeHelper.GetChild(d, i));
                var tb = d as TextBlock;
                if (tb == null || !tb.IsVisible || string.IsNullOrEmpty(tb.Text) || tb.ActualWidth <= 0) continue;
                double w = tb.ActualWidth, h = tb.ActualHeight, maxH = tb.MaxHeight;
                tb.MaxHeight = double.PositiveInfinity;   // a card cuts its text at two lines: that counts as not fitting too
                tb.Measure(new Size(tb.TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : w + tb.Margin.Left + tb.Margin.Right,
                    double.PositiveInfinity));
                double needW = tb.DesiredSize.Width - tb.Margin.Left - tb.Margin.Right, needH = tb.DesiredSize.Height - tb.Margin.Top - tb.Margin.Bottom;
                tb.MaxHeight = maxH;
                if (needW > w + 1 || needH > h + 1)
                    Log.Write("overflow, tab " + tab + ": \"" + tb.Text.Replace("\n", " ") + "\" needs " + Math.Ceiling(needW) + "x" +
                        Math.Ceiling(needH) + ", has " + Math.Floor(w) + "x" + Math.Floor(h));
            }
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); };
        }

        // ---- small building blocks ----

        TextBlock Gray(string text, double size)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Hue("#8E8E93"), TextWrapping = TextWrapping.Wrap };
        }

        // a section title; the optional right-hand text sits on the same line
        Grid Section(string title, out TextBlock right, double top)
        {
            var g = new Grid { Margin = new Thickness(0, top, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
            right = new TextBlock { FontSize = 12, Foreground = Hue("#8E8E93"), HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
            return g;
        }

        // title on the left, value on the right, the slider under them, one grey line of explanation
        SliderRow AddSlider(Panel into, string title, string id, string hint, string hintOff, Func<int> get, Action<int> set, bool glassOnly, Action after)
        {
            var root = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            TextBlock val;
            var line = Section(title, out val, 0);
            line.Margin = new Thickness(0, 0, 0, 2);
            val.FontSize = 12.5;
            val.Foreground = Hue("#D1D1D6");
            var s = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, Value = get(), IsMoveToPointEnabled = true };
            AutomationProperties.SetAutomationId(s, id);
            AutomationProperties.SetName(s, title);
            var h = Gray(hint, 11.5);
            h.Margin = new Thickness(0, 0, 0, 0);
            var row = new SliderRow { Slider = s, Value = val, Hint = h, HintOn = hint, HintOff = hintOff, GlassOnly = glassOnly, Get = get };
            val.Text = (int)s.Value + "%";
            s.ValueChanged += delegate
            {
                int v = (int)Math.Round(s.Value);
                val.Text = v + "%";
                if (loading) return;
                set(v);
                after();
            };
            root.Children.Add(line);
            root.Children.Add(s);
            root.Children.Add(h);
            into.Children.Add(root);
            rows.Add(row);
            return row;
        }

        // ---- tabs ----

        StackPanel BuildGlassTab()
        {
            var p = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            TextBlock none;
            p.Children.Add(Section(L.T("settings.quality"), out none, 0));
            var seg = new UniformGrid { Rows = 1 };
            var segHost = new Border { Background = Hue("#1C1C1E"), CornerRadius = new CornerRadius(9), Padding = new Thickness(3), Child = seg };
            for (int i = 0; i < Qualities.Length; i++)
            {
                int idx = i;
                var rb = new RadioButton { Content = Qualities[i], GroupName = "quality", Style = (Style)FindResource("Seg"), IsChecked = i == Settings.Quality, Margin = new Thickness(i > 0 ? 2 : 0, 0, 0, 0) };
                rb.Checked += delegate { if (!loading) { Settings.Quality = idx; Changed(); } };
                AutomationProperties.SetAutomationId(rb, "quality" + i);
                qualityButtons.Add(rb);
                seg.Children.Add(rb);
            }
            p.Children.Add(segHost);
            qualityNote.FontSize = 11.5;
            qualityNote.Foreground = Hue("#8E8E93");
            qualityNote.TextWrapping = TextWrapping.Wrap;
            qualityNote.Margin = new Thickness(2, 6, 0, 0);
            p.Children.Add(qualityNote);

            // who draws the banners: the graphics card from capture to screen (GpuEngine), or WPF as in 1.0
            TextBlock unused;
            p.Children.Add(Section(L.T("settings.engine"), out unused, 16));
            var engineSeg = new UniformGrid { Rows = 1 };
            var engineHost = new Border { Background = Hue("#1C1C1E"), CornerRadius = new CornerRadius(9), Padding = new Thickness(3), Child = engineSeg };
            string[] engines = { "gpu", "classic" };
            for (int i = 0; i < engines.Length; i++)
            {
                string engine = engines[i];
                var rb = new RadioButton
                {
                    Content = L.T("engine." + engine), Tag = engine, GroupName = "engine", Style = (Style)FindResource("Seg"),
                    IsChecked = Settings.Engine == engine, Margin = new Thickness(i > 0 ? 2 : 0, 0, 0, 0)
                };
                rb.Checked += delegate { if (!loading) { Settings.Engine = engine; Manager.EngineChanged(); Changed(); } };
                AutomationProperties.SetAutomationId(rb, "engine-" + engine);
                engineButtons.Add(rb);
                engineSeg.Children.Add(rb);
            }
            p.Children.Add(engineHost);
            engineNote.FontSize = 11.5;
            engineNote.Foreground = Hue("#8E8E93");
            engineNote.TextWrapping = TextWrapping.Wrap;
            engineNote.Margin = new Thickness(2, 6, 0, 0);
            p.Children.Add(engineNote);

            // live background: its own switch, because following the screen all the time (also while a banner moves) costs
            liveRow.Margin = new Thickness(0, 16, 0, 0);
            liveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            liveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var liveText = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            liveText.Children.Add(new TextBlock { Text = L.T("settings.live"), FontSize = 13, FontWeight = FontWeights.SemiBold });
            liveText.Children.Add(Gray(L.T("settings.liveHint"), 11.5));
            liveRow.Children.Add(liveText);
            liveSwitch.Style = (Style)FindResource("Switch");
            liveSwitch.IsChecked = Settings.LiveBackground;
            liveSwitch.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(liveSwitch, L.T("settings.live"));
            AutomationProperties.SetAutomationId(liveSwitch, "liveSwitch");
            liveSwitch.Checked += delegate { if (!loading) { Settings.LiveBackground = true; Changed(); } };
            liveSwitch.Unchecked += delegate { if (!loading) { Settings.LiveBackground = false; Changed(); } };
            Grid.SetColumn(liveSwitch, 1);
            liveRow.Children.Add(liveSwitch);
            p.Children.Add(liveRow);

            // how often the live glass of a banner that stands still is captured again
            liveFpsRow.Margin = new Thickness(0, 10, 0, 0);
            var fpsLine = new Grid();
            fpsLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fpsLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fpsLine.Children.Add(new TextBlock { Text = L.T("settings.liveFps"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), TextWrapping = TextWrapping.Wrap });
            var fpsSeg = new UniformGrid { Rows = 1 };
            var fpsHost = new Border { Background = Hue("#1C1C1E"), CornerRadius = new CornerRadius(9), Padding = new Thickness(3), Child = fpsSeg, Width = 184 };
            for (int i = 0; i < Settings.LiveRates.Length; i++)
            {
                int rate = Settings.LiveRates[i];
                var rb = new RadioButton
                {
                    Content = rate.ToString(), GroupName = "livefps", Style = (Style)FindResource("Seg"), IsChecked = rate == Settings.LiveFps,
                    Margin = new Thickness(i > 0 ? 2 : 0, 0, 0, 0)
                };
                rb.Checked += delegate { if (!loading) { Settings.LiveFps = rate; Changed(); } };
                AutomationProperties.SetName(rb, L.T("settings.liveFps") + " " + rate);
                AutomationProperties.SetAutomationId(rb, "livefps" + rate);
                fpsButtons.Add(rb);
                fpsSeg.Children.Add(rb);
            }
            Grid.SetColumn(fpsHost, 1);
            fpsLine.Children.Add(fpsHost);
            liveFpsRow.Children.Add(fpsLine);
            fpsHint.FontSize = 11.5;
            fpsHint.Foreground = Hue("#8E8E93");
            fpsHint.TextWrapping = TextWrapping.Wrap;
            fpsHint.Margin = new Thickness(0, 5, 0, 0);
            liveFpsRow.Children.Add(fpsHint);
            p.Children.Add(liveFpsRow);

            p.Children.Add(Section(L.T("settings.color"), out colorName, 18));
            var wrap = new WrapPanel();
            foreach (var sw in Swatches)
            {
                string spec = sw[1], name = sw[0];
                Color c;
                bool isColor = Settings.TryParseColor(spec, out c);
                var g = new Grid { Width = 30, Height = 30, Margin = new Thickness(0, 0, 3, 2), Cursor = Cursors.Hand, Tag = spec, ToolTip = name, Background = Brushes.Transparent };
                g.Children.Add(new Ellipse { Stroke = Hue("#0A84FF"), StrokeThickness = 2, Opacity = 0 });
                var fill = new Ellipse
                {
                    Width = 22, Height = 22, StrokeThickness = 1, Stroke = Hue("#40FFFFFF"),
                    Fill = isColor ? (Brush)new SolidColorBrush(c) : new LinearGradientBrush(Color.FromRgb(0x1E, 0x1E, 0x20), Color.FromRgb(0xF5, 0xF5, 0xF7), 45)
                };
                g.Children.Add(fill);
                g.MouseLeftButtonUp += delegate
                {
                    Settings.ColorSpec = spec;
                    loading = true; hex.Text = spec == "auto" ? "" : spec; loading = false;
                    Changed();
                };
                swatchViews.Add(g);
                wrap.Children.Add(g);
            }
            p.Children.Add(wrap);

            var hexRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lbl = Gray(L.T("settings.customColor"), 12);
            lbl.TextWrapping = TextWrapping.NoWrap;   // a short label in a column as wide as it needs
            lbl.VerticalAlignment = VerticalAlignment.Center;
            lbl.Margin = new Thickness(0, 0, 10, 0);
            hex.Text = Settings.ColorSpec == "auto" ? "" : Settings.ColorSpec;
            // in a right-to-left sentence the "#" of #RRGGBB would jump to the other end: keep the code a left-to-right run
            hex.ToolTip = L.Rtl ? L.T("settings.hexTip").Replace("#RRGGBB", "‪#RRGGBB‬") : L.T("settings.hexTip");
            hex.FlowDirection = FlowDirection.LeftToRight;   // a hex code reads left to right in every language
            AutomationProperties.SetAutomationId(hex, "hex");
            AutomationProperties.SetName(hex, L.T("settings.customColor"));
            hex.TextChanged += delegate
            {
                Color c;
                bool ok = Settings.TryParseColor(hex.Text, out c);
                hexChip.Background = ok ? new SolidColorBrush(c) : null;
                if (loading) return;
                if (ok)
                {
                    Settings.ColorSpec = "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
                    Changed();
                }
            };
            hexChip.Margin = new Thickness(10, 0, 0, 0);
            hexChip.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(hex, 1);
            Grid.SetColumn(hexChip, 2);
            var hint = Gray("#RRGGBB", 11.5);
            hint.FlowDirection = FlowDirection.LeftToRight;
            hint.TextWrapping = TextWrapping.NoWrap;
            hint.HorizontalAlignment = HorizontalAlignment.Left;   // next to the swatch, also when the panel reads right to left
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(hint, 3);
            hexRow.Children.Add(lbl);
            hexRow.Children.Add(hex);
            hexRow.Children.Add(hexChip);
            hexRow.Children.Add(hint);
            p.Children.Add(hexRow);

            AddSlider(p, L.T("slider.tint"), "tint", L.T("slider.tint.note"), L.T("settings.needsGlass"),
                delegate { return Settings.TintIntensity; }, delegate(int v) { Settings.TintIntensity = v; }, true, Changed);
            AddSlider(p, L.T("slider.glass"), "glass", L.T("slider.glass.note"), L.T("settings.needsGlass"),
                delegate { return Settings.Intensity; }, delegate(int v) { Settings.Intensity = v; }, true, Changed);
            AddSlider(p, L.T("slider.blur"), "blur", L.T("slider.blur.note"), L.T("settings.needsGlass"),
                delegate { return Settings.Blur; }, delegate(int v) { Settings.Blur = v; }, true, Changed);
            return p;
        }

        StackPanel BuildShadowTab()
        {
            var p = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            AddSlider(p, L.T("slider.shadow"), "shadow", L.T("slider.shadow.note"), "",
                delegate { return Settings.ShadowIntensity; }, delegate(int v) { Settings.ShadowIntensity = v; }, false, Changed);
            AddSlider(p, L.T("slider.softness"), "softness", L.T("slider.softness.note"), "",
                delegate { return Settings.ShadowSoftness; }, delegate(int v) { Settings.ShadowSoftness = v; }, false, Changed);
            AddSlider(p, L.T("slider.offset"), "offset", L.T("slider.offset.note"), "",
                delegate { return Settings.ShadowOffset; }, delegate(int v) { Settings.ShadowOffset = v; }, false, Changed);
            var note = Gray(L.T("settings.shadowHint"), 11.5);
            note.Margin = new Thickness(0, 22, 0, 0);
            p.Children.Add(note);
            return p;
        }

        StackPanel BuildSoundTab()
        {
            var p = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(new TextBlock { Text = L.T("settings.sound"), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            soundOn.Style = (Style)FindResource("Switch");
            soundOn.IsChecked = Settings.Sound;
            AutomationProperties.SetName(soundOn, L.T("settings.sound"));
            AutomationProperties.SetAutomationId(soundOn, "soundSwitch");
            soundOn.Checked += delegate { SoundChanged(); };
            soundOn.Unchecked += delegate { SoundChanged(); };
            Grid.SetColumn(soundOn, 1);
            top.Children.Add(soundOn);
            p.Children.Add(top);

            TextBlock none;
            p.Children.Add(Section(L.T("settings.melody"), out none, 16));
            for (int i = 0; i < Chime.Names.Length; i++)
            {
                int idx = i;
                var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = L.T("chime." + i), FontSize = 13, FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock { Text = SoundNotes[i], FontSize = 11.5, Foreground = Hue("#8E8E93") });
                var rb = new RadioButton { Content = text, GroupName = "melody", Style = (Style)FindResource("Melody"), IsChecked = i == Settings.SoundStyle };
                AutomationProperties.SetAutomationId(rb, "melody" + i);
                AutomationProperties.SetName(rb, L.T("chime." + i));
                rb.Checked += delegate
                {
                    if (loading) return;
                    SoundChanged();
                    PlayPreview(idx);
                };
                var play = new Button { Style = (Style)FindResource("Play"), Margin = new Thickness(-40, 0, 10, 0), ToolTip = L.T("settings.listen") };
                AutomationProperties.SetName(play, L.F("settings.listenTo", L.T("chime." + i)));
                play.Click += delegate { PlayPreview(idx); };
                Grid.SetColumnSpan(rb, 2);
                Grid.SetColumn(play, 1);
                rowGrid.Children.Add(rb);
                rowGrid.Children.Add(play);
                melodyButtons.Add(rb);
                soundDependents.Add(rb);
                soundDependents.Add(play);
                p.Children.Add(rowGrid);
            }

            var volRoot = new StackPanel();
            AddSlider(volRoot, L.T("slider.volume"), "soundvol", L.T("slider.volume.note"), "",
                delegate { return Settings.SoundVolume; }, delegate(int v) { Settings.SoundVolume = v; }, false, SoundChanged);
            p.Children.Add(volRoot);
            soundDependents.Add(volRoot);

            var adaptRow = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            adaptRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            adaptRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var adaptText = new StackPanel();
            adaptText.Children.Add(new TextBlock { Text = L.T("settings.adapt"), FontSize = 13 });
            adaptText.Children.Add(Gray(L.T("settings.adaptHint"), 11.5));
            adaptRow.Children.Add(adaptText);
            soundAdapt.Style = (Style)FindResource("Switch");
            soundAdapt.IsChecked = Settings.SoundAdapt;
            soundAdapt.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(soundAdapt, L.T("settings.adapt"));
            AutomationProperties.SetAutomationId(soundAdapt, "adaptSwitch");
            soundAdapt.Checked += delegate { SoundChanged(); };
            soundAdapt.Unchecked += delegate { SoundChanged(); };
            Grid.SetColumn(soundAdapt, 1);
            adaptRow.Children.Add(soundAdapt);
            p.Children.Add(adaptRow);
            soundDependents.Add(adaptRow);
            return p;
        }

        void ShowTab(int index)
        {
            tab = index;
            // Hidden, not Collapsed: the other tabs keep their room, so the window measures the same on every tab
            for (int i = 0; i < tabPanels.Count; i++) tabPanels[i].Visibility = i == index ? Visibility.Visible : Visibility.Hidden;
        }

        // ---- applying settings ----

        // glass, colour, quality, shadow: save, update the controls and re-bake the preview
        void Changed()
        {
            if (loading) return;
            Settings.Glass = master.IsChecked == true;
            Settings.Save();
            Refresh();
            if (Gpu.Ok) Gpu.Shader(Settings.Quality);   // a level used for the first time compiles its lens first, not timed
            RebakeAll();
        }

        void SoundChanged()
        {
            if (loading) return;
            Settings.Sound = soundOn.IsChecked == true;
            Settings.SoundAdapt = soundAdapt.IsChecked == true;
            for (int i = 0; i < melodyButtons.Count; i++) if (melodyButtons[i].IsChecked == true) Settings.SoundStyle = i;
            Settings.Save();
            Refresh();
        }

        // picking a melody (or pressing its play button) plays it with the current settings
        void PlayPreview(int style)
        {
            Settings.SoundStyle = style;
            Look look = Look.FromSettings();
            Manager.PlayChime(look);
        }

        // brings the enabled/disabled state and the texts in line with the settings
        void Refresh()
        {
            qualityNote.Text = QualityNotes[Math.Max(0, Math.Min(3, Settings.Quality))];
            liveRow.IsEnabled = Settings.Glass;
            // the graphics card follows the screen at its own refresh rate: the rate is the classic way's only
            bool gpu = Settings.Engine == "gpu" && !GpuEngine.Failed;
            engineNote.Text = Settings.Engine == "gpu" && GpuEngine.Failed ? L.T("settings.engineFailed") : L.T("settings.engineHint");
            liveFpsRow.IsEnabled = Settings.Glass && Settings.LiveBackground;
            fpsHint.Text = gpu ? L.T("settings.liveFpsGpu") : L.T("settings.liveFpsHint");
            foreach (var rb in fpsButtons) rb.IsEnabled = !gpu;
            foreach (var r in rows)
            {
                bool off = r.GlassOnly && !Settings.Glass;
                r.Slider.IsEnabled = !off;
                r.Hint.Text = off ? r.HintOff : r.HintOn;
            }
            string name = Settings.ColorSpec == "auto" ? L.T("color.autoName") : L.T("settings.customColor");
            foreach (var g in swatchViews)
            {
                bool sel = (string)g.Tag == Settings.ColorSpec;
                ((Ellipse)g.Children[0]).Opacity = sel ? 1 : 0;
                if (sel && Settings.ColorSpec != "auto") foreach (var sw in Swatches) if (sw[1] == Settings.ColorSpec) name = sw[0];
            }
            colorName.Text = name;
            bool snd = Settings.Sound;
            foreach (var e in soundDependents) e.IsEnabled = snd;
        }

        // puts the settings into the controls (after a reset)
        void Sync()
        {
            loading = true;
            master.IsChecked = Settings.Glass;
            for (int i = 0; i < qualityButtons.Count; i++) qualityButtons[i].IsChecked = i == Settings.Quality;
            hex.Text = Settings.ColorSpec == "auto" ? "" : Settings.ColorSpec;
            liveSwitch.IsChecked = Settings.LiveBackground;
            foreach (var rb in fpsButtons) rb.IsChecked = (string)rb.Content == Settings.LiveFps.ToString();
            foreach (var rb in engineButtons) rb.IsChecked = (string)rb.Tag == Settings.Engine;
            foreach (var r in rows) { r.Slider.Value = r.Get(); r.Value.Text = (int)r.Slider.Value + "%"; }
            soundOn.IsChecked = Settings.Sound;
            soundAdapt.IsChecked = Settings.SoundAdapt;
            for (int i = 0; i < melodyButtons.Count; i++) melodyButtons[i].IsChecked = i == Settings.SoundStyle;
            loading = false;
            Settings.Save();
            Refresh();
            Manager.EngineChanged();
            if (Gpu.Ok) Gpu.Shader(Settings.Quality);
            RebakeAll();
        }

        void ResetTab()
        {
            if (tab == 0) { Settings.Quality = 2; Settings.Engine = "gpu"; Settings.LiveBackground = false; Settings.LiveFps = 30; Settings.ColorSpec = "auto"; Settings.TintIntensity = 60; Settings.Intensity = 60; Settings.Blur = 35; }
            else if (tab == 1) { Settings.ShadowIntensity = 40; Settings.ShadowSoftness = 60; Settings.ShadowOffset = 40; }
            else { Settings.Sound = true; Settings.SoundStyle = 1; Settings.SoundVolume = 65; Settings.SoundAdapt = true; }
            Sync();
        }

        void ResetAll()
        {
            Settings.Glass = true;
            Settings.Quality = 2; Settings.Engine = "gpu"; Settings.LiveBackground = false; Settings.LiveFps = 30; Settings.ColorSpec = "auto"; Settings.TintIntensity = 60; Settings.Intensity = 60; Settings.Blur = 35;
            Settings.ShadowIntensity = 40; Settings.ShadowSoftness = 60; Settings.ShadowOffset = 40;
            Settings.Sound = true; Settings.SoundStyle = 1; Settings.SoundVolume = 65; Settings.SoundAdapt = true;
            Sync();
        }

        // what the glass costs right now: the last re-bake of the three cards, and the load of the whole program
        void ShowTiming()
        {
            var now = DateTime.UtcNow;
            self.Refresh();
            var cpu = self.TotalProcessorTime;
            double span = (now - lastCpuAt).TotalMilliseconds;
            string use = span > 100 && lastCpuAt != default(DateTime)
                ? L.F("timing.cpu", Math.Round(100 * (cpu - lastCpu).TotalMilliseconds / span))
                : "";
            lastCpu = cpu;
            lastCpuAt = now;
            string bake = lastBakeMs < 0 ? "" : L.F("timing.bake", lastBakeMs.ToString("0.0"));
            timing.Text = bake + (bake.Length > 0 && use.Length > 0 ? "  ·  " : "") + use;
        }

        // ---- scene and cards ----

        void AddCard(GlassCard c, double x, double y)
        {
            c.LayoutNow();
            Canvas.SetLeft(c, x);
            Canvas.SetTop(c, y);
            c.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                dragging = c;
                dragStart = e.GetPosition(canvas);
                dragOrigin = new Point(Canvas.GetLeft(c), Canvas.GetTop(c));
                c.CaptureMouse();
            };
            c.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (dragging != c) return;
                Point p = e.GetPosition(canvas);
                Canvas.SetLeft(c, Math.Max(-20, Math.Min(SW - c.Width + 20, dragOrigin.X + p.X - dragStart.X)));
                Canvas.SetTop(c, Math.Max(-20, Math.Min(SH - c.CardH + 20, dragOrigin.Y + p.Y - dragStart.Y)));
                Rebake(c);
            };
            c.MouseLeftButtonUp += delegate { dragging = null; c.ReleaseMouseCapture(); };
            cards.Add(c);
            canvas.Children.Add(c);
        }

        void Rebake(GlassCard c)
        {
            if (sceneBuf == null) return;
            var rect = new Rect(Canvas.GetLeft(c), Canvas.GetTop(c), c.Width, c.CardH);
            Cap cap = c.Look.Glass ? Glass.FromBuffer(sceneBuf, sceneW, sceneH, rect, scale, c.Look) : null;
            c.Bake(cap, scale);
        }

        void RebakeAll()
        {
            var sw = Stopwatch.StartNew();
            var look = Look.FromSettings();
            foreach (var c in cards) { c.Look = look; Rebake(c); }
            lastBakeMs = sw.Elapsed.TotalMilliseconds;
        }

        void SetScene(int preset)
        {
            var art = BuildScene(preset);
            art.Measure(new Size(SW, SH));
            art.Arrange(new Rect(0, 0, SW, SH));
            sceneW = (int)Math.Round(SW * scale);
            sceneH = (int)Math.Round(SH * scale);
            var rtb = new RenderTargetBitmap(sceneW, sceneH, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(art);
            rtb.Freeze();
            sceneBuf = new byte[sceneW * sceneH * 4];
            rtb.CopyPixels(sceneBuf, sceneW * 4, 0);
            sceneImage.Source = rtb;
            if (!loading) RebakeAll();
        }

        static FrameworkElement BuildScene(int preset)
        {
            var c = new Canvas { Width = SW, Height = SH };
            Func<byte, byte, byte, Color> rgb = delegate(byte r, byte g, byte b) { return Color.FromRgb(r, g, b); };
            Action<double, double, double, double, Brush> rect = delegate(double x, double y, double w, double h, Brush br)
            {
                var r = new Rectangle { Width = w, Height = h, Fill = br };
                Canvas.SetLeft(r, x); Canvas.SetTop(r, y); c.Children.Add(r);
            };
            Action<double, double, double, Color, double> blob = delegate(double x, double y, double d, Color col, double alpha)
            {
                var e = new Ellipse
                {
                    Width = d, Height = d,
                    Fill = new RadialGradientBrush(Color.FromArgb((byte)(255 * alpha), col.R, col.G, col.B), Color.FromArgb(0, col.R, col.G, col.B))
                };
                Canvas.SetLeft(e, x - d / 2); Canvas.SetTop(e, y - d / 2); c.Children.Add(e);
            };
            Action<Brush, double> lines = delegate(Brush fg, double size)
            {
                for (double y = 8; y < SH; y += size * 1.45)
                {
                    var t = new TextBlock { Text = "lorem ipsum dolor sit amet, consectetur adipiscing elit sed do eiusmod tempor", FontFamily = new FontFamily("Consolas"), FontSize = size, Foreground = fg };
                    Canvas.SetLeft(t, 8); Canvas.SetTop(t, y); c.Children.Add(t);
                }
            };

            if (preset == 0)
            {
                rect(0, 0, SW, SH, new LinearGradientBrush(rgb(0x2B, 0x1B, 0x6B), rgb(0xFF, 0x7A, 0x59), 55));
                blob(110, 110, 430, rgb(0xFF, 0xD1, 0x66), 0.9);
                blob(430, 150, 400, rgb(0xFF, 0x3D, 0x9A), 0.85);
                blob(260, 380, 480, rgb(0x2F, 0xA8, 0xFF), 0.8);
                blob(470, 440, 340, rgb(0x6E, 0xF0, 0xB8), 0.7);
                blob(50, 450, 280, rgb(0xA0, 0x5C, 0xFF), 0.8);
            }
            else if (preset == 1)
            {
                rect(0, 0, SW, SH, Brushes.White);
                string[] cols = { "#E53935", "#FB8C00", "#FDD835", "#43A047", "#039BE5", "#5E35B1", "#D81B60" };
                for (int i = 0; i < 28; i++)
                    rect(i * 19, 0, 19, SH, new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(cols[i % cols.Length])));
                rect(0, 140, SW, 110, Brushes.White);
                rect(0, 250, SW, 110, Brushes.Black);
                lines(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), 12);
            }
            else if (preset == 2)
            {
                rect(0, 0, SW, SH, new SolidColorBrush(rgb(0xF4, 0xF4, 0xF6)));
                lines(new SolidColorBrush(rgb(0x30, 0x30, 0x34)), 13);
                blob(430, 110, 250, rgb(0x0A, 0x84, 0xFF), 0.6);
                blob(90, 400, 290, rgb(0xFF, 0x9F, 0x0A), 0.6);
            }
            else
            {
                rect(0, 0, SW, SH, new SolidColorBrush(rgb(0x0C, 0x0C, 0x12)));
                blob(130, 130, 380, rgb(0x5E, 0x5C, 0xE6), 0.8);
                blob(430, 290, 400, rgb(0xFF, 0x37, 0x5F), 0.6);
                blob(220, 420, 360, rgb(0x30, 0xD1, 0x58), 0.5);
                lines(new SolidColorBrush(Color.FromArgb(0x70, 0xC8, 0xC8, 0xD0)), 12);
            }
            return c;
        }
    }
}
