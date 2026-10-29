using Avalonia.Data;
using Avalonia.Markup.Xaml;
using PowerOfChaos.Launcher.Core.Localization;

namespace PowerOfChaos.Launcher.Localization;

/// <summary>One live binding per authored label; all labels share the finite English-key catalog.</summary>
public sealed class TrExtension : MarkupExtension
{
    public string Text { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(LocalizedTextEntry.Value))
    {
        Source = LauncherText.Current.Entry(Text),
        Mode = BindingMode.OneWay
    };
}
