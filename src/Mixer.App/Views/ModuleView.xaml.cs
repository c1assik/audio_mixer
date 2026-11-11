using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mixer.App.Ui;
using Mixer.App.ViewModels;
using Mixer.Core.Config;

namespace Mixer.App.Views;

/// <summary>
/// Модуль 3U темы Modular вместо карточки узла: панель с винтами, надписи капсом, ручка LEVEL, столбик светодиодов,
/// тумблер MUTE, подписи обработки со светодиодами. Источники алюминиевые, шины чёрные, выходы кремовые. Модель та же,
/// что у карточки (<see cref="NodeViewModel"/>); гнёзда и кабели рисует <see cref="RackView"/>.
/// </summary>
public partial class ModuleView : UserControl
{
    private NodeViewModel? _vm;

    public ModuleView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as NodeViewModel;
            if (_vm is null) return;
            _vm.PropertyChanged += OnVmChanged;
            ApplyColors();
            Update();
        };
        Level.DragCompleted += () => _vm?.EndEdit();
        Level.ResetRequested += () => _vm?.ResetGainCommand.Execute(null);
    }

    /// <summary>Панель и надписи по колонке узла.</summary>
    public static (string Panel, string Ink, string Muted) ColorsFor(NodeColumn c) => c switch
    {
        NodeColumn.Sources => ("PanelAlu", "PanelInk", "PanelMuted"),
        NodeColumn.Buses => ("PanelBlack", "PanelInkLight", "PanelMutedLight"),
        _ => ("PanelCream", "PanelInk", "PanelMuted"),
    };

    private void ApplyColors()
    {
        if (_vm is null) return;
        var (panel, ink, muted) = ColorsFor(_vm.Column);
        Panel.Background = Res(panel, Brushes.LightGray);
        var inkBrush = Res(ink, Brushes.Black);
        var mutedBrush = Res(muted, Brushes.Gray);
        Title.Foreground = inkBrush;
        LevelLabel.Foreground = inkBrush;
        MuteLabel.Foreground = inkBrush;
        Level.Foreground = inkBrush;
        Sub.Foreground = mutedBrush;
        LevelValue.Foreground = mutedBrush;
        State.Foreground = mutedBrush;
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NodeViewModel.Name) or nameof(NodeViewModel.Subtitle) or nameof(NodeViewModel.Look) or nameof(NodeViewModel.Message)
                or nameof(NodeViewModel.Warnings) or nameof(NodeViewModel.IsDimmed) or nameof(NodeViewModel.MicForText)
                or nameof(NodeViewModel.IsSelected) or nameof(NodeViewModel.LimiterText):
                Update();
                break;
        }
    }

    private void Update()
    {
        if (_vm is not { } n) return;
        string culture = System.Globalization.CultureInfo.CurrentCulture.Name;
        Title.Text = n.Name.ToUpper(System.Globalization.CultureInfo.CurrentCulture);
        Sub.Text = n.Subtitle.ToUpper(System.Globalization.CultureInfo.CurrentCulture);
        string? state = n.Look != NodeLook.Normal ? n.Message : n.Warnings ?? n.MicForText ?? n.LimiterText;
        State.Text = state ?? "";
        State.ToolTip = string.IsNullOrEmpty(state) ? null : state;
        State.Foreground = n.Look == NodeLook.Error || n.Warnings is not null && n.Look == NodeLook.Normal
            ? Res("Red", Brushes.Red)
            : Res(ColorsFor(n.Column).Muted, Brushes.Gray);
        Opacity = n.IsDimmed ? 0.45 : n.Look == NodeLook.Normal ? 1 : 0.75;
        Panel.BorderBrush = n.IsSelected ? Res("Accent", Brushes.Orange) : new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
        Panel.BorderThickness = new Thickness(n.IsSelected ? 2 : 1);
    }
}
