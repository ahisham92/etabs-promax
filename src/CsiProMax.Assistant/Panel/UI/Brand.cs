using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CsiProMax.UI;

/// <summary>
/// The product look for CSI ProMax windows: logo, the light/dark theme palette, control styles and a few shared
/// building blocks. Every colour lives in <see cref="Tokens"/> (a light and a dark value per token); windows refer to
/// tokens by resource key only, so they follow ETABS's UI theme and switch live when it changes.
/// The loader's pane mirrors this palette (CsiProMax.Loader.Theme, which can't reference the core): keep both in step.
/// </summary>
internal static class Brand
{
    /// <summary>The header band's gradient: the same in both themes (it is the brand, with white text on it).</summary>
    public static readonly Color Blue = Color.FromRgb(0x1D, 0x4E, 0xD8);
    public static readonly Color Navy = Color.FromRgb(0x0B, 0x1E, 0x4F);

    /// <summary>Resource keys of the palette. Use them with SetResourceReference so colours follow the theme.</summary>
    public static class K
    {
        public const string Bg = "SpmBg", Surface = "SpmSurface", SurfaceHover = "SpmSurfaceHover", Border = "SpmBorder";
        public const string Field = "SpmField", InputBorder = "SpmInputBorder", Text = "SpmText", Muted = "SpmMuted";
        public const string Accent = "SpmAccent", AccentHover = "SpmAccentHover", AccentPressed = "SpmAccentPressed", OnAccent = "SpmOnAccent";
        public const string Link = "SpmLink", Danger = "SpmDanger", DangerHover = "SpmDangerHover", DangerPressed = "SpmDangerPressed";
        public const string OnDanger = "SpmOnDanger", DangerText = "SpmDangerText", SuccessText = "SpmSuccessText", WarningText = "SpmWarningText";
        public const string Focus = "SpmFocus", NoticeBg = "SpmNoticeBg", NoticeBorder = "SpmNoticeBorder";
        public const string BadgeOk = "SpmBadgeOk", BadgeWarn = "SpmBadgeWarn", BadgeMuted = "SpmBadgeMuted", BadgeError = "SpmBadgeError";
        public const string HeaderText = "SpmHeaderText", HeaderSub = "SpmHeaderSub";
        // The chat's softer surfaces: raised cards and the composer, hairline borders, the accent tint of your messages.
        public const string Raised = "SpmRaised", BorderSubtle = "SpmBorderSubtle", AccentSoft = "SpmAccentSoft";
        // The composer's message box: a field a shade off the card, so it reads as the place to type.
        public const string ComposerField = "SpmComposerField";
        /// <summary>The chat's conversation area (cards and the message box sit raised on it) and the soft fills of its status badges.</summary>
        public const string Canvas = "SpmCanvas", SuccessSoft = "SpmSuccessSoft", DangerSoft = "SpmDangerSoft";
        /// <summary>ETABS's chrome: toolbar and pane strips, the menu bar, window tabs, group box captions, button faces, table grids.</summary>
        public const string Strip = "SpmStrip", MenuBar = "SpmMenuBar", StripBorder = "SpmStripBorder", TabOutline = "SpmTabOutline";
        public const string TabActiveTop = "SpmTabActiveTop", TabActiveBottom = "SpmTabActiveBottom", TabActiveBorder = "SpmTabActiveBorder";
        public const string GroupCaption = "SpmGroupCaption", ButtonFace = "SpmButtonFace", ButtonBorder = "SpmButtonBorder", ButtonPressed = "SpmButtonPressed";
        public const string GridHeader = "SpmGridHeader", GridLine = "SpmGridLine";
    }

    /// <summary>
    /// The palette: (key, light, dark). CSI ProMax looks like ETABS 23, which has one look, so both columns hold the same
    /// ETABS colours (measured from the ETABS 23.2 manuals' screenshots: menu bar #F7F7F7, toolbars, pane strips and the
    /// status bar #D8D8D8, forms #F0F0F0, button face #E1E1E1 on #ADADAD, list selection #0078D7, group box captions
    /// #00008B). The dark column is kept so a later dark ETABS can be followed without touching every window.
    /// Text pairs meet WCAG AA 4.5:1: Text on Bg 21:1, on Surface 18:1, on Strip 14:1; Muted on Surface 6.3:1; OnAccent on Accent 4.5:1.
    /// </summary>
    public static readonly (string Key, uint Light, uint Dark)[] Tokens = Same(
    [
        (K.Bg, 0xFFFFFF),
        (K.Surface, 0xF0F0F0),
        (K.SurfaceHover, 0xE5F1FB),
        (K.Border, 0xADADAD),
        (K.Field, 0xFFFFFF),
        (K.InputBorder, 0x7A7A7A),
        (K.Text, 0x000000),
        (K.Muted, 0x555555),
        (K.Accent, 0x0078D7),
        (K.AccentHover, 0x005A9E),
        (K.AccentPressed, 0x004578),
        (K.OnAccent, 0xFFFFFF),
        (K.Link, 0x0066CC),
        (K.Danger, 0xC42B1C),
        (K.DangerHover, 0xA32317),
        (K.DangerPressed, 0x7F1B12),
        (K.OnDanger, 0xFFFFFF),
        (K.DangerText, 0xA61B0F),
        (K.SuccessText, 0x1D6B2E),
        (K.WarningText, 0x8A5A00),
        (K.Focus, 0x0078D7),
        (K.NoticeBg, 0xFFF8DC),
        (K.NoticeBorder, 0xD9B44A),
        (K.BadgeOk, 0x1D6B2E),
        (K.BadgeWarn, 0x8A5A00),
        (K.BadgeMuted, 0x555555),
        (K.BadgeError, 0xA61B0F),
        (K.HeaderText, 0x000000),
        (K.HeaderSub, 0x333333),
        (K.Raised, 0xFFFFFF),
        (K.BorderSubtle, 0xDCDCDC),
        // Your messages: a grey band across the list, as in the design, not a blue bubble.
        (K.AccentSoft, 0xF3F3F3),
        (K.ComposerField, 0xFFFFFF),
        (K.Canvas, 0xFFFFFF),
        (K.SuccessSoft, 0xE8F5EA),
        (K.DangerSoft, 0xFDE7E4),
        // ETABS's own chrome.
        (K.Strip, 0xD8D8D8),
        (K.MenuBar, 0xF7F7F7),
        (K.StripBorder, 0xB0B0B0),
        (K.TabOutline, 0x696969),
        (K.TabActiveTop, 0xB4CCE6),
        (K.TabActiveBottom, 0x9EB9D5),
        (K.TabActiveBorder, 0x99B4D1),
        (K.GroupCaption, 0x00008B),
        (K.ButtonFace, 0xE1E1E1),
        (K.ButtonBorder, 0xADADAD),
        (K.ButtonPressed, 0xCCE4F7),
        (K.GridHeader, 0xF0F0F0),
        (K.GridLine, 0xE1E1E1),
    ]);

    private static (string, uint, uint)[] Same((string Key, uint Value)[] list) => list.Select(t => (t.Key, t.Value, t.Value)).ToArray();

    public enum Weight { Secondary, Primary, Danger }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Rgb(uint v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    // ---- Theme -------------------------------------------------------------------------------------------------

    /// <summary>True when ETABS's UI theme is dark (ETABS 2024+). The last theme read when it can't be read now (light at first).</summary>
    private static bool _lastDark;

    public static bool IsDark
    {
        get
        {
            try { return _lastDark = ReadDark(); }
            catch (Exception) { return _lastDark; } // not readable right now: keep the last theme read
        }
    }

    /// <summary>ETABS 23 has one light look and CSI ProMax matches it, whatever Windows' app mode is.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReadDark() => false;

    /// <summary>The ETABS font: Segoe UI 9 pt (12 px) everywhere.</summary>
    public static readonly FontFamily Font = new("Segoe UI");
    public const double FontSize = 12;

    private static readonly List<FrameworkElement> Themed = [];
    private static readonly ConditionalWeakTable<FrameworkElement, object> DarkShown = new();
    private static DispatcherTimer? _poll;
    private static ResourceDictionary? _styles;

    /// <summary>
    /// Gives a window (or any root) the palette and the control styles, and keeps it in step with ETABS's theme: a
    /// cheap check every 2 s while it is open (the core gets no theme event of its own) and whenever it is activated.
    /// Safe to call more than once.
    /// </summary>
    public static void UseTheme(FrameworkElement root)
    {
        var styles = Styles();
        if (!root.Resources.MergedDictionaries.Contains(styles)) root.Resources.MergedDictionaries.Add(styles);
        Apply(root, IsDark);
        TextElement.SetFontFamily(root, Font);
        TextElement.SetFontSize(root, FontSize);
        if (root is Control control)
        {
            control.SetResourceReference(Control.BackgroundProperty, K.Bg);
            control.SetResourceReference(Control.ForegroundProperty, K.Text);
        }
        if (Themed.Contains(root)) return;
        Themed.Add(root);
        if (root is Window window)
        {
            window.Activated += (_, _) => Recheck();
            window.Closed += (_, _) =>
            {
                Themed.Remove(root);
                if (Themed.Count == 0) _poll?.Stop();
            };
        }
        if (_poll is null)
        {
            _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _poll.Tick += (_, _) => Recheck();
        }
        _poll.Start();
    }

    /// <summary>Stops following the theme for a root that is going away (the chat element of a core being replaced).</summary>
    public static void ReleaseTheme(FrameworkElement root)
    {
        Themed.Remove(root);
        if (Themed.Count == 0) _poll?.Stop();
    }

    /// <summary>Shows a root in the given theme now, whatever ETABS's is (the chat's DPI snapshot draws both).</summary>
    public static void ShowTheme(FrameworkElement root, bool dark) => Apply(root, dark);

    private static void Recheck()
    {
        var dark = IsDark;
        foreach (var root in Themed.ToList()) Apply(root, dark);
    }

    private static void Apply(FrameworkElement root, bool dark)
    {
        if (DarkShown.TryGetValue(root, out var was) && (bool)was == dark) return;
        DarkShown.Remove(root);
        DarkShown.Add(root, dark);
        foreach (var (key, light, darkValue) in Tokens) root.Resources[key] = Brush(Rgb(dark ? darkValue : light));
    }

    /// <summary>
    /// The palette as a new dictionary of frozen brushes, for a window on a thread of its own (the chat's job window),
    /// which can't share this thread's resources. Read <see cref="IsDark"/> on ETABS's thread and pass it in.
    /// </summary>
    public static ResourceDictionary Palette(bool dark)
    {
        var dictionary = new ResourceDictionary();
        foreach (var (key, light, darkValue) in Tokens) dictionary[key] = Brush(Rgb(dark ? darkValue : light));
        return dictionary;
    }

    /// <summary>Points an element's text colour at a palette token.</summary>
    public static T Fg<T>(T element, string key) where T : FrameworkElement
    {
        element.SetResourceReference(TextElement.ForegroundProperty, key);
        return element;
    }

    /// <summary>A wrapping text block in a palette colour.</summary>
    public static TextBlock Text(string text, string key = K.Text, double size = 12, bool bold = false) =>
        Fg(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        }, key);

    // ---- Styles ------------------------------------------------------------------------------------------------

    private static string ButtonStyle(string key, string fg, string border, double thickness) => $$"""
        <Style x:Key="{{key}}" TargetType="Button" BasedOn="{StaticResource SpmButtonBase}">
          <Setter Property="Background" Value="{DynamicResource SpmButtonFace}"/>
          <Setter Property="Foreground" Value="{DynamicResource {{fg}}}"/>
          <Setter Property="BorderBrush" Value="{DynamicResource {{border}}}"/>
          <Setter Property="BorderThickness" Value="{{thickness}}"/>
          <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter Property="Background" Value="{DynamicResource SpmSurfaceHover}"/>
              <Setter Property="BorderBrush" Value="{DynamicResource SpmAccent}"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter Property="Background" Value="{DynamicResource SpmButtonPressed}"/>
              <Setter Property="BorderBrush" Value="{DynamicResource SpmAccentPressed}"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Background" Value="{DynamicResource SpmSurface}"/>
              <Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/>
              <Setter Property="BorderBrush" Value="{DynamicResource SpmBorderSubtle}"/>
            </Trigger>
          </Style.Triggers>
        </Style>
        """;

    /// <summary>
    /// Control styles in the ETABS 23 look: the square Windows controls ETABS's forms use (grey button faces with a blue
    /// hover, white square fields, plain check boxes and radio buttons, group boxes with dark blue captions, flat tabs),
    /// Segoe UI 9 pt. Templates are replaced so every state uses palette tokens only.
    /// </summary>
    private static ResourceDictionary Styles()
    {
        if (_styles is not null) return _styles;
        var xaml = $$$"""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style x:Key="SpmFocusVisual">
                <Setter Property="Control.Template"><Setter.Value><ControlTemplate>
                  <Rectangle Margin="2" StrokeThickness="1" Stroke="{DynamicResource SpmText}" StrokeDashArray="1 2" SnapsToDevicePixels="True"/>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style x:Key="SpmButtonBase" TargetType="Button">
                <Setter Property="FocusVisualStyle" Value="{StaticResource SpmFocusVisual}"/>
                <Setter Property="MinHeight" Value="23"/>
                <Setter Property="MinWidth" Value="23"/>
                <Setter Property="Padding" Value="10,2"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="HorizontalContentAlignment" Value="Center"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}" SnapsToDevicePixels="True">
                      <ContentPresenter Margin="{TemplateBinding Padding}" RecognizesAccessKey="True"
                                        HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                        VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                    </Border>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
              {{{ButtonStyle("SpmSecondaryButton", K.Text, K.ButtonBorder, 1)}}}
              {{{ButtonStyle("SpmPrimaryButton", K.Text, K.Accent, 2)}}}
              {{{ButtonStyle("SpmDangerButton", K.DangerText, K.ButtonBorder, 1)}}}
              <Style TargetType="Button" BasedOn="{StaticResource SpmSecondaryButton}"/>

              <Style TargetType="TextBox">
                <Setter Property="Background" Value="{DynamicResource SpmField}"/>
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource SpmInputBorder}"/>
                <Setter Property="CaretBrush" Value="{DynamicResource SpmText}"/>
                <Setter Property="SelectionBrush" Value="{DynamicResource SpmAccent}"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Padding" Value="3,2"/>
                <Setter Property="MinHeight" Value="22"/>
                <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="TextBox">
                    <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}" SnapsToDevicePixels="True">
                      <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False"
                                    HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource SpmText}"/></Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource SpmFocus}"/></Trigger>
                      <Trigger Property="IsEnabled" Value="False">
                        <Setter TargetName="bd" Property="Background" Value="{DynamicResource SpmSurface}"/>
                        <Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>

              <Style TargetType="CheckBox">
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="MinHeight" Value="20"/>
                <Setter Property="FocusVisualStyle" Value="{StaticResource SpmFocusVisual}"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="CheckBox">
                    <Grid Background="Transparent">
                      <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                      <Border x:Name="box" Width="13" Height="13" BorderThickness="1" VerticalAlignment="Center"
                              BorderBrush="{DynamicResource SpmText}" Background="{DynamicResource SpmField}" SnapsToDevicePixels="True">
                        <Path x:Name="mark" Data="M1.5,5.5 L4.5,8.5 L9.5,2" Stroke="{DynamicResource SpmText}" StrokeThickness="1.6" Visibility="Collapsed"/>
                      </Border>
                      <ContentPresenter Grid.Column="1" Margin="6,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource SpmAccent}"/>
                        <Setter TargetName="box" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/>
                      </Trigger>
                      <Trigger Property="IsChecked" Value="True"><Setter TargetName="mark" Property="Visibility" Value="Visible"/></Trigger>
                      <Trigger Property="IsEnabled" Value="False">
                        <Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/>
                        <Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource SpmBorder}"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>

              <Style TargetType="RadioButton">
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="MinHeight" Value="20"/>
                <Setter Property="FocusVisualStyle" Value="{StaticResource SpmFocusVisual}"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="RadioButton">
                    <Grid Background="Transparent">
                      <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                      <Grid Width="13" Height="13" VerticalAlignment="Center">
                        <Ellipse x:Name="ring" Stroke="{DynamicResource SpmText}" StrokeThickness="1" Fill="{DynamicResource SpmField}"/>
                        <Ellipse x:Name="dot" Width="5" Height="5" Fill="{DynamicResource SpmText}" Visibility="Collapsed"/>
                      </Grid>
                      <ContentPresenter Grid.Column="1" Margin="6,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="ring" Property="Stroke" Value="{DynamicResource SpmAccent}"/></Trigger>
                      <Trigger Property="IsChecked" Value="True"><Setter TargetName="dot" Property="Visibility" Value="Visible"/></Trigger>
                      <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>

              <Style TargetType="ComboBoxItem">
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="MinHeight" Value="20"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="ComboBoxItem">
                    <Border x:Name="bd" Background="Transparent" Padding="4,2" SnapsToDevicePixels="True">
                      <ContentPresenter VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsHighlighted" Value="True">
                        <Setter TargetName="bd" Property="Background" Value="{DynamicResource SpmAccent}"/>
                        <Setter Property="Foreground" Value="{DynamicResource SpmOnAccent}"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>

              <Style TargetType="ComboBox">
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="MinHeight" Value="22"/>
                <Setter Property="FocusVisualStyle" Value="{StaticResource SpmFocusVisual}"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="ComboBox">
                    <Grid>
                      <ToggleButton Focusable="False" ClickMode="Press"
                                    IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                        <ToggleButton.Template>
                          <ControlTemplate TargetType="ToggleButton">
                            <Border x:Name="bd" Background="{DynamicResource SpmField}" BorderBrush="{DynamicResource SpmInputBorder}"
                                    BorderThickness="1" SnapsToDevicePixels="True">
                              <Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,6,0" Data="M0,0 L3.5,3.5 L7,0"
                                    Stroke="{DynamicResource SpmText}" StrokeThickness="1"/>
                            </Border>
                            <ControlTemplate.Triggers>
                              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource SpmAccent}"/></Trigger>
                            </ControlTemplate.Triggers>
                          </ControlTemplate>
                        </ToggleButton.Template>
                      </ToggleButton>
                      <ContentPresenter IsHitTestVisible="False" Margin="4,2,20,2" VerticalAlignment="Center"
                                        Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
                      <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False"
                             IsOpen="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}">
                        <Border MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                                MaxHeight="{TemplateBinding MaxDropDownHeight}" Background="{DynamicResource SpmField}"
                                BorderBrush="{DynamicResource SpmText}" BorderThickness="1">
                          <ScrollViewer><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                        </Border>
                      </Popup>
                    </Grid>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{DynamicResource SpmMuted}"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>

              <Style TargetType="GroupBox">
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="Background" Value="{DynamicResource SpmSurface}"/>
                <Setter Property="Padding" Value="8,18,8,8"/>
                <Setter Property="Margin" Value="0,0,0,8"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="GroupBox">
                    <Grid>
                      <Border Margin="0,7,0,0" BorderThickness="1" BorderBrush="{DynamicResource SpmBorderSubtle}" SnapsToDevicePixels="True"/>
                      <Border HorizontalAlignment="Left" VerticalAlignment="Top" Margin="7,0,0,0" Padding="3,0" Background="{TemplateBinding Background}">
                        <ContentPresenter ContentSource="Header" TextElement.Foreground="{DynamicResource SpmGroupCaption}" RecognizesAccessKey="True"/>
                      </Border>
                      <ContentPresenter Margin="{TemplateBinding Padding}" ContentSource="Content"/>
                    </Grid>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>

              <Style TargetType="TabControl">
                <Setter Property="Background" Value="{DynamicResource SpmBg}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource SpmBorder}"/>
                <Setter Property="BorderThickness" Value="0,1,0,0"/>
                <Setter Property="Padding" Value="0"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="TabControl">
                    <DockPanel>
                      <Border DockPanel.Dock="Top" Background="{DynamicResource SpmSurface}" Padding="3,3,3,0">
                        <TabPanel IsItemsHost="True" Panel.ZIndex="1" KeyboardNavigation.TabIndex="1"/>
                      </Border>
                      <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Margin="0,-1,0,0">
                        <ContentPresenter ContentSource="SelectedContent" Margin="{TemplateBinding Padding}"/>
                      </Border>
                    </DockPanel>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
              <Style TargetType="TabItem">
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="FocusVisualStyle" Value="{StaticResource SpmFocusVisual}"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="TabItem">
                    <Border x:Name="bd" Background="Transparent" BorderBrush="Transparent" BorderThickness="1,1,1,0" Padding="9,2,9,3" Margin="0,0,1,0" SnapsToDevicePixels="True">
                      <ContentPresenter ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/></Trigger>
                      <Trigger Property="IsSelected" Value="True">
                        <Setter TargetName="bd" Property="Background" Value="{DynamicResource SpmBg}"/>
                        <Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource SpmBorder}"/>
                        <Setter TargetName="bd" Property="Margin" Value="0,0,1,-1"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>

              <Style TargetType="DataGrid">
                <Setter Property="Background" Value="{DynamicResource SpmBg}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource SpmBorder}"/>
                <Setter Property="HorizontalGridLinesBrush" Value="{DynamicResource SpmGridLine}"/>
                <Setter Property="VerticalGridLinesBrush" Value="{DynamicResource SpmGridLine}"/>
                <Setter Property="RowHeaderWidth" Value="0"/>
              </Style>
              <Style TargetType="DataGridColumnHeader">
                <Setter Property="Background" Value="{DynamicResource SpmGridHeader}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource SpmBorder}"/>
                <Setter Property="BorderThickness" Value="0,0,1,1"/>
                <Setter Property="Padding" Value="5,2"/>
              </Style>

              <Style TargetType="ProgressBar">
                <Setter Property="Foreground" Value="{DynamicResource SpmSuccessText}"/>
                <Setter Property="Background" Value="{DynamicResource SpmSurface}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource SpmBorder}"/>
              </Style>
              <Style TargetType="Hyperlink">
                <Setter Property="Foreground" Value="{DynamicResource SpmLink}"/>
                <Setter Property="FocusVisualStyle" Value="{StaticResource SpmFocusVisual}"/>
              </Style>
            </ResourceDictionary>
            """;
        // Never let a styling problem stop a window or the pane from opening: fall back to the plain WPF look.
        try { _styles = (ResourceDictionary)XamlReader.Parse(xaml); }
        catch (Exception) { _styles = new ResourceDictionary(); }
        return _styles;
    }

    // ---- Logo and window chrome --------------------------------------------------------------------------------

    private static BitmapSource? _logo;
    private static BitmapFrame? _icon;

    /// <summary>The 64 px logo, embedded in the core.</summary>
    public static BitmapSource? Logo => _logo ??= Load("CsiProMax.logo-64.png");

    /// <summary>The window icon (.ico).</summary>
    public static BitmapFrame? Icon
    {
        get
        {
            if (_icon is not null) return _icon;
            try
            {
                using var stream = typeof(Brand).Assembly.GetManifestResourceStream("CsiProMax.csi-promax.ico");
                if (stream is null) return null;
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                _icon = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 32)).FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException) { }
            return _icon;
        }
    }

    private static BitmapSource? Load(string name)
    {
        try
        {
            using var stream = typeof(Brand).Assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Applies the window icon and the theme, and makes ETABS the owner, so the window stays with ETABS and never
    /// hides behind it.
    /// </summary>
    public static void Prepare(Window window)
    {
        if (Icon is { } icon) window.Icon = icon;
        window.ShowInTaskbar = false;
        UseTheme(window);
        // ETABS's forms are grey (#F0F0F0), with white fields and lists on them.
        window.SetResourceReference(Control.BackgroundProperty, K.Surface);
        // The CSI ProMax window owns its dialogs and tool windows, so they stay with it and never hide behind ETABS.
        var main = Host.MainWindow.Handle;
        if (main != IntPtr.Zero)
        {
            try { new WindowInteropHelper(window).Owner = main; }
            catch (Exception) { /* no owner: still works */ }
        }
    }

    /// <summary>
    /// The title of a CSI ProMax window, drawn like an ETABS docked pane: a grey strip with the title on an outlined tab,
    /// and the subtitle as a plain line below it on the form's grey.
    /// </summary>
    public static Border Header(string title, string? subtitle)
    {
        var stack = new StackPanel();
        stack.Children.Add(Strip(title));
        if (subtitle is not null)
        {
            var sub = Text(subtitle, K.HeaderSub);
            sub.Margin = new Thickness(10, 6, 10, 2);
            stack.Children.Add(sub);
        }
        var band = new Border { Child = stack };
        band.SetResourceReference(Border.BackgroundProperty, K.Surface);
        return band;
    }

    /// <summary>An ETABS pane strip: #D8D8D8 with the title on a tab outlined in #696969 (the look of Model Explorer's own tab).</summary>
    public static Border Strip(string title, UIElement? right = null)
    {
        var row = new DockPanel { LastChildFill = false, Height = 22 };
        var tabRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (Icon is { } icon)
        {
            var image = new Image { Source = icon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(image, "CSI ProMax");
            tabRow.Children.Add(image);
        }
        tabRow.Children.Add(Text(title, K.Text));
        var tab = new Border
        {
            Child = tabRow, BorderThickness = new Thickness(1, 1, 1, 0), CornerRadius = new CornerRadius(3, 3, 0, 0),
            Padding = new Thickness(14, 1, 14, 2), Margin = new Thickness(4, 3, 0, 0), VerticalAlignment = VerticalAlignment.Bottom,
        };
        tab.SetResourceReference(Border.BorderBrushProperty, K.TabOutline);
        row.Children.Add(tab);
        if (right is not null)
        {
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
        }
        var strip = new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1) };
        strip.SetResourceReference(Border.BackgroundProperty, K.Strip);
        strip.SetResourceReference(Border.BorderBrushProperty, K.StripBorder);
        return strip;
    }

    /// <summary>A group box as ETABS draws them in its forms: a thin frame with a dark blue caption.</summary>
    public static GroupBox Group(string caption, UIElement content, string background = K.Surface)
    {
        var box = new GroupBox { Header = caption, Content = content };
        box.SetResourceReference(Control.BackgroundProperty, background);
        return box;
    }

    /// <summary>
    /// A dialog button. Each window has one <see cref="Weight.Primary"/> action (make it IsDefault); destructive
    /// actions (delete, overwrite, roll back, cancel a job, stop) are <see cref="Weight.Danger"/> and never IsDefault.
    /// </summary>
    public static Button Button(string text, Weight weight = Weight.Secondary)
    {
        var button = new Button { Content = text, MinWidth = 80, Margin = new Thickness(4, 0, 4, 0) };
        button.SetResourceReference(FrameworkElement.StyleProperty, weight switch
        {
            Weight.Primary => "SpmPrimaryButton",
            Weight.Danger => "SpmDangerButton",
            _ => "SpmSecondaryButton",
        });
        return button;
    }

    /// <summary>The button row at the foot of a form, centred as ETABS's forms have it (OK, Cancel).</summary>
    public static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    // ---- Busy state --------------------------------------------------------------------------------------------

    private static readonly ConditionalWeakTable<Button, object> BusyContent = new();

    /// <summary>True while <see cref="SetBusy"/> shows the button as working.</summary>
    public static bool IsBusy(Button button) => BusyContent.TryGetValue(button, out _);

    /// <summary>
    /// Shows a button as working: full colour (not the disabled look) with a small spinner and an optional label such
    /// as "Stopping…". Clicks are ignored until it is set back. No-op when it is already busy.
    /// </summary>
    public static void SetBusy(Button button, bool busy, string? text = null)
    {
        if (busy)
        {
            if (BusyContent.TryGetValue(button, out var original)) return; // already working: keep the spinner turning
            original = button.Content ?? "";
            BusyContent.Add(button, original);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Spinner());
            row.Children.Add(new TextBlock { Text = text ?? original as string ?? "Working…", VerticalAlignment = VerticalAlignment.Center });
            button.Content = row;
            button.IsHitTestVisible = false;
            AutomationProperties.SetItemStatus(button, "Busy");
        }
        else if (BusyContent.TryGetValue(button, out var original))
        {
            BusyContent.Remove(button);
            button.Content = original;
            button.IsHitTestVisible = true;
            AutomationProperties.SetItemStatus(button, "");
        }
    }

    /// <summary>A 12 px spinning arc in the text colour of whatever holds it.</summary>
    public static FrameworkElement Spinner()
    {
        var arc = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M6,1 A5,5 0 1 1 1,6"), StrokeThickness = 2, Width = 12, Height = 12, Stretch = Stretch.None,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        arc.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), RelativeSource = RelativeSource.Self });
        var spin = new RotateTransform();
        arc.RenderTransform = spin;
        WhileVisible(arc,
            () => spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever }),
            () => spin.BeginAnimation(RotateTransform.AngleProperty, null));
        AutomationProperties.SetName(arc, "Working");
        return arc;
    }

    /// <summary>
    /// Runs a looping animation only while <paramref name="element"/> is on screen: a clock that repeats forever keeps
    /// WPF drawing every frame and stays alive with its element, so one left on a hidden, collapsed or discarded element
    /// (a step's spinner replaced by a check, a closed pane) would never stop.
    /// </summary>
    public static void WhileVisible(UIElement element, Action start, Action stop)
    {
        element.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) start();
            else stop();
        };
        if (element.IsVisible) start();
    }
}
